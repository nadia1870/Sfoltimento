using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Costruisce le slice di esecuzione.
///
/// Regola fondamentale per i Collective: tutti gli OrderId appartenenti allo
/// stesso CollectiveOrderId sono una singola unita' di planning e ricevono lo
/// stesso BatchNo. In questo modo il SliceExecutor puo' cancellare l'intero
/// aggregato, compreso il Collective, in una sola transazione.
/// </summary>
// Resta su SqlExecutor concreto, e non e' una dimenticanza.
//
// Il pianificatore ha bisogno della stessa connessione per piu' statement,
// perche' la tabella temporanea #assignments vive sulla sessione. Ma non ha
// bisogno di una transazione: avvolgerlo in una IPurgeSession terrebbe lock
// sullo staging per l'intera pianificazione, che e' un cambio di comportamento
// e non appartiene a una migrazione meccanica.
//
// E' una terza forma — connessione condivisa senza transazione — che le due
// interfacce attuali non coprono. Va decisa a parte, non risolta di straforo.
public sealed class BatchPlanner(
    SqlExecutor sql,
    PurgeStrategyResolver strategyResolver,
    ILogger<BatchPlanner> log,
    int flushEvery = 50_000,
    int bulkCopyTimeoutSeconds = 300,
    int maxAggregateWeight = 0)
{
    private readonly int _flushEvery = flushEvery > 0
        ? flushEvery
        : throw new ArgumentOutOfRangeException(nameof(flushEvery));

    private readonly int _bulkCopyTimeoutSeconds = bulkCopyTimeoutSeconds >= 0
        ? bulkCopyTimeoutSeconds
        : throw new ArgumentOutOfRangeException(nameof(bulkCopyTimeoutSeconds));

    /// <summary>Tetto per aggregato: zero lo disattiva. Vedi PurgeOptions.MaxAggregateWeight.</summary>
    private readonly int _maxAggregateWeight = maxAggregateWeight >= 0
        ? maxAggregateWeight
        : throw new ArgumentOutOfRangeException(nameof(maxAggregateWeight));

    public async Task<int> PlanAsync(PurgeRun run, CancellationToken ct)
    {
        var strategy = strategyResolver.Resolve(run.Strategy);
        if (strategy.PlanningMode == PurgePlanningMode.OrphanHistory)
            return await PlanOrphansAsync(run, ct).ConfigureAwait(false);
        await using var conn = await sql.OpenAsync(ct).ConfigureAwait(false);
        await using (var create = sql.Command(conn, null, RetentionSql.CreateAssignmentTempTable))
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var buffer = NewTable();
        var packer = new BatchPacker(run.MaxRowsPerBatch, run.MaxOrdersPerBatch, _maxAggregateWeight);
        void AddAssignments(IReadOnlyList<BatchPacker.Assignment> assignments)
        {
            foreach (var assignment in assignments)
            {
                buffer.Rows.Add(assignment.OrderId, assignment.BatchNo,
                    assignment.IsOversized, assignment.CollectiveOrderId is Guid id ? id : DBNull.Value,
                    assignment.Excluded);
            }

        }

        // Il DataReader non puo' restare aperto mentre SqlBulkCopy usa la stessa
        // SqlConnection: ogni reader viene chiuso prima del flush e la lettura
        // riparte dall'ultima chiave. Il packer sopravvive alle pagine, quindi
        // un collettivo a cavallo del confine resta una sola unita'.
        async Task FlushIfFullAsync()
        {
            if (buffer.Rows.Count < _flushEvery) return;
            await FlushAsync(conn, buffer, ct).ConfigureAwait(false);
            buffer = NewTable();
        }

        // Passata 1 — standalone, keyset su OrderId.
        var lastOrderId = Guid.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = 0;

            await using (var cmd = sql.Command(conn, null,
                            RetentionSql.ReadStandaloneCandidatesPage,
                            SqlParam.Of("@RunId", run.RunId),
                            SqlParam.Typed("@PageSize", _flushEvery, SqlDbType.Int),
                            SqlParam.Typed("@LastOrderId", lastOrderId, SqlDbType.UniqueIdentifier)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var orderId = reader.GetGuid(0);
                    AddAssignments(packer.Add(new BatchPacker.Candidate(
                        orderId, reader.IsDBNull(1) ? 1 : reader.GetInt32(1), null)));

                    lastOrderId = orderId;
                    read++;
                }
            }

            await FlushIfFullAsync().ConfigureAwait(false);

            // Pagina incompleta: la sorgente e' esaurita. Stessa condizione di
            // uscita di BatchedStatementRunner, e risparmia la query a vuoto
            // che serviva a scoprire la fine.
            if (read < _flushEvery) break;
        }

        // Passata 2 — componenti dei collettivi, keyset su (CollectiveOrderId, OrderId).
        var lastCollectiveOrderId = Guid.Empty;
        lastOrderId = Guid.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = 0;

            await using (var cmd = sql.Command(conn, null,
                            RetentionSql.ReadCollectiveCandidatesPage,
                            SqlParam.Of("@RunId", run.RunId),
                            SqlParam.Typed("@PageSize", _flushEvery, SqlDbType.Int),
                            SqlParam.Typed("@LastCollectiveOrderId", lastCollectiveOrderId, SqlDbType.UniqueIdentifier),
                            SqlParam.Typed("@LastOrderId", lastOrderId, SqlDbType.UniqueIdentifier)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var orderId = reader.GetGuid(0);
                    var collectiveOrderId = reader.GetGuid(2);

                    AddAssignments(packer.Add(new BatchPacker.Candidate(
                        orderId, reader.IsDBNull(1) ? 1 : reader.GetInt32(1), collectiveOrderId)));

                    lastCollectiveOrderId = collectiveOrderId;
                    lastOrderId = orderId;
                    read++;
                }
            }

            await FlushIfFullAsync().ConfigureAwait(false);

            if (read < _flushEvery) break;
        }

        AddAssignments(packer.Complete());
        if (buffer.Rows.Count > 0)
            await FlushAsync(conn, buffer, ct).ConfigureAwait(false);

        await using (var apply = sql.Command(conn, null, RetentionSql.ApplyAssignments,
                        SqlParam.Of("@RunId", run.RunId)))
            await apply.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (!run.DryRun)
        {
            await using var init = sql.Command(conn, null, RetentionSql.InitializeBatchProgress,
                SqlParam.Of("@RunId", run.RunId));
            await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var sliceCount = packer.SliceCount;
        log.LogInformation(
            "PurgePlanningCompleted RunId={RunId} Ordini={Orders} Slice={Slices} " +
            "Oversized={Oversized} MaxRighe={MaxRows} MaxOrdini={MaxOrders} CollectiveAtomic={CollectiveAtomic}",
            run.RunId, packer.Total, sliceCount, packer.OversizedCount, run.MaxRowsPerBatch, run.MaxOrdersPerBatch,
            run.Strategy == RetentionStrategy.Collective);
        if (packer.TooLargeCount > 0)
        {
            log.LogWarning(
                "PurgeAggregateTooLarge RunId={RunId} Aggregati={Count} Tetto={Limite}: " +
                "oltre il tetto per aggregato, esclusi e censiti, restano a database. " +
                "Se il numero non e' trascurabile, il tetto va rivisto o quegli aggregati " +
                "vanno trattati a parte.",
                run.RunId, packer.TooLargeCount, _maxAggregateWeight);
        }

        if (packer.OversizedCount > 0)
        {
            log.LogWarning(
                "RunId={RunId}: {Count} aggregati oversized. Per i Collective l'intero " +
                "aggregato resta comunque nella stessa transazione.", run.RunId, packer.OversizedCount);
        }

        return sliceCount;
    }

    private async Task<int> PlanOrphansAsync(PurgeRun run, CancellationToken ct)
    {
        await using var conn = await sql.OpenAsync(ct).ConfigureAwait(false);
        await using (var create = sql.Command(conn, null, RetentionSql.CreateOrphanAssignmentTempTable))
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        var table = new DataTable();
        table.Columns.Add("OrderHistoryId", typeof(Guid));
        table.Columns.Add("BatchNo", typeof(int));
        var batchNo = 0;
        var inBatch = 0;
        var total = 0;
        var lastOrderHistoryId = Guid.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = 0;

            await using (var cmd = sql.Command(conn, null, RetentionSql.ReadOrphansForPlanningPage,
                            SqlParam.Of("@RunId", run.RunId),
                            SqlParam.Typed("@PageSize", _flushEvery, SqlDbType.Int),
                            SqlParam.Typed("@LastOrderHistoryId", lastOrderHistoryId, SqlDbType.UniqueIdentifier)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetGuid(0);
                    if (inBatch >= run.MaxOrdersPerBatch) { batchNo++; inBatch = 0; }
                    table.Rows.Add(id, batchNo);
                    inBatch++;
                    total++;
                    read++;
                    lastOrderHistoryId = id;
                }
            }

            if (table.Rows.Count >= _flushEvery)
            {
                await FlushOrphansAsync(conn, table, ct).ConfigureAwait(false);
                table.Clear();
            }

            if (read < _flushEvery) break;
        }

        if (table.Rows.Count > 0)
            await FlushOrphansAsync(conn, table, ct).ConfigureAwait(false);
        await using (var apply = sql.Command(conn, null, RetentionSql.ApplyOrphanAssignments, SqlParam.Of("@RunId", run.RunId)))
            await apply.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // Stessa guardia del percorso degli ordini: un dry-run non deve lasciare
        // slice 'Pending' in RunBatchProgress, che e' la coda di lavoro
        // dell'esecuzione reale.
        if (!run.DryRun)
        {
            await using var init = sql.Command(conn, null, RetentionSql.InitializeOrphanBatchProgress,
                SqlParam.Of("@RunId", run.RunId));
            await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var slices = total == 0 ? 0 : batchNo + 1;
        log.LogInformation("PurgeOrphanPlanningCompleted RunId={RunId} Storici={Count} Slice={Slices}",
            run.RunId, total, slices);
        return slices;
    }

    private async Task FlushOrphansAsync(SqlConnection conn, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "#assignOrphan",
            BulkCopyTimeout = _bulkCopyTimeoutSeconds
        };
        bulk.ColumnMappings.Add("OrderHistoryId", "OrderHistoryId");
        bulk.ColumnMappings.Add("BatchNo", "BatchNo");
        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }

    private static DataTable NewTable()
    {
        var t = new DataTable();
        t.Columns.Add("OrderId", typeof(Guid));
        t.Columns.Add("BatchNo", typeof(int));
        t.Columns.Add("IsOversized", typeof(bool));
        t.Columns.Add("CollectiveOrderId", typeof(Guid));
        t.Columns.Add("Excluded", typeof(bool));
        return t;
    }

    private async Task FlushAsync(SqlConnection conn, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "#assign",
            BulkCopyTimeout = _bulkCopyTimeoutSeconds
        };
        bulk.ColumnMappings.Add("OrderId", "OrderId");
        bulk.ColumnMappings.Add("BatchNo", "BatchNo");
        bulk.ColumnMappings.Add("IsOversized", "IsOversized");
        bulk.ColumnMappings.Add("CollectiveOrderId", "CollectiveOrderId");
        bulk.ColumnMappings.Add("Excluded", "Excluded");

        // I mapping sono espliciti di proposito: senza, SqlBulkCopy va per
        // posizione e una colonna aggiunta in mezzo scriverebbe nel posto
        // sbagliato in silenzio. Il prezzo e' che una colonna nuova va
        // aggiunta in tre punti — DataTable, temp table, questa lista — e
        // dimenticare qui produce un NOT NULL violato al primo flush.
        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }
}
