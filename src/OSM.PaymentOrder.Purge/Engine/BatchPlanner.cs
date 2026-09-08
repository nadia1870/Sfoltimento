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
    int flushEvery = 50_000)
{
    private readonly int _flushEvery = flushEvery > 0
        ? flushEvery
        : throw new ArgumentOutOfRangeException(nameof(flushEvery));

    private sealed record Candidate(Guid OrderId, int Weight, Guid? CollectiveOrderId);

    public async Task<int> PlanAsync(PurgeRun run, CancellationToken ct)
    {
        var strategy = strategyResolver.Resolve(run.Strategy);
        if (strategy.PlanningMode == PurgePlanningMode.OrphanHistory)
            return await PlanOrphansAsync(run, ct).ConfigureAwait(false);

        await using var conn = await sql.OpenAsync(ct).ConfigureAwait(false);
        await using (var create = sql.Command(conn, null, RetentionSql.CreateAssignmentTempTable))
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var buffer = NewTable();
        var batchNo = 0;
        var rowsInBatch = 0;
        var ordersInBatch = 0;
        var oversized = 0;
        var total = 0;
        Guid? currentCollective = null;
        var collectiveBuffer = new List<Candidate>();
        var collectiveWeight = 0;

        async Task FlushCollectiveAsync()
        {
            if (collectiveBuffer.Count == 0)
                return;

            var collectiveId = collectiveBuffer[0].CollectiveOrderId;
            if (collectiveId is null)
                throw new InvalidOperationException("Collective buffer senza CollectiveOrderId.");

            var collectiveOrders = collectiveBuffer.Count;
            var isOversized = collectiveWeight > run.MaxRowsPerBatch ||
                              collectiveOrders > run.MaxOrdersPerBatch;

            if (isOversized ||
                (ordersInBatch > 0 &&
                 (rowsInBatch + collectiveWeight > run.MaxRowsPerBatch ||
                  ordersInBatch + collectiveOrders > run.MaxOrdersPerBatch)))
            {
                if (ordersInBatch > 0)
                {
                    batchNo++;
                    rowsInBatch = 0;
                    ordersInBatch = 0;
                }
            }

            foreach (var candidate in collectiveBuffer)
                buffer.Rows.Add(candidate.OrderId, batchNo, isOversized, collectiveId.Value);

            rowsInBatch += collectiveWeight;
            ordersInBatch += collectiveOrders;

            if (isOversized)
            {
                oversized++;
                batchNo++;
                rowsInBatch = 0;
                ordersInBatch = 0;
            }

            collectiveBuffer.Clear();
            collectiveWeight = 0;
            currentCollective = null;

        }

        async Task AssignStandaloneAsync(Candidate candidate)
        {
            var isOversized = candidate.Weight > run.MaxRowsPerBatch || 1 > run.MaxOrdersPerBatch;
            if (isOversized)
            {
                if (ordersInBatch > 0)
                {
                    batchNo++;
                    rowsInBatch = 0;
                    ordersInBatch = 0;
                }

                buffer.Rows.Add(candidate.OrderId, batchNo, true, DBNull.Value);
                oversized++;
                batchNo++;
                rowsInBatch = 0;
                ordersInBatch = 0;
            }
            else
            {
                var wouldExceedRows = rowsInBatch + candidate.Weight > run.MaxRowsPerBatch;
                var wouldExceedOrders = ordersInBatch + 1 > run.MaxOrdersPerBatch;
                if (ordersInBatch > 0 && (wouldExceedRows || wouldExceedOrders))
                {
                    batchNo++;
                    rowsInBatch = 0;
                    ordersInBatch = 0;
                }

                buffer.Rows.Add(candidate.OrderId, batchNo, false, DBNull.Value);
                rowsInBatch += candidate.Weight;
                ordersInBatch++;
            }

        }

        // Il DataReader non puo' restare aperto mentre SqlBulkCopy usa la stessa
        // SqlConnection. Leggiamo quindi pagine keyset: ogni reader viene chiuso
        // prima del flush e poi la lettura riparte dall'ultima chiave ordinata.
        var hasAnchor = false;
        Guid? lastOrderId = null;
        Guid? lastCollectiveOrderId = null;
        var lastSortGroup = 0;

        while (true)
        {
            var pageRead = false;

            await using (var read = sql.Command(conn, null,
                            RetentionSql.ReadCandidatesForPlanningPage,
                            SqlParam.Of("@RunId", run.RunId),
                            SqlParam.Typed("@PageSize", _flushEvery, SqlDbType.Int),
                            SqlParam.Typed("@HasAnchor", hasAnchor, SqlDbType.Bit),
                            SqlParam.Typed("@LastSortGroup", lastSortGroup, SqlDbType.Int),
                            SqlParam.Typed("@LastCollectiveOrderId", lastCollectiveOrderId, SqlDbType.UniqueIdentifier),
                            SqlParam.Typed("@LastOrderId", lastOrderId, SqlDbType.UniqueIdentifier)))
            await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    pageRead = true;
                    var candidate = new Candidate(
                        reader.GetGuid(0),
                        reader.IsDBNull(1) ? 1 : reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetGuid(2));
                    total++;

                    if (candidate.CollectiveOrderId is Guid collectiveId)
                    {
                        if (currentCollective is null)
                            currentCollective = collectiveId;
                        else if (currentCollective != collectiveId)
                            await FlushCollectiveAsync().ConfigureAwait(false);

                        currentCollective ??= collectiveId;
                        collectiveBuffer.Add(candidate);
                        collectiveWeight += candidate.Weight;
                    }
                    else
                    {
                        await FlushCollectiveAsync().ConfigureAwait(false);
                        await AssignStandaloneAsync(candidate).ConfigureAwait(false);
                    }

                    hasAnchor = true;
                    lastOrderId = candidate.OrderId;
                    lastCollectiveOrderId = candidate.CollectiveOrderId;
                    lastSortGroup = candidate.CollectiveOrderId.HasValue ? 1 : 0;
                }
            }

            // Il reader e' gia' stato disposed: da questo punto SqlBulkCopy puo'
            // usare in sicurezza la stessa connection e le #temp table di sessione.
            if (buffer.Rows.Count >= _flushEvery)
            {
                await FlushAsync(conn, buffer, ct).ConfigureAwait(false);
                buffer = NewTable();
            }

            if (!pageRead)
                break;
        }

        await FlushCollectiveAsync().ConfigureAwait(false);

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

        var sliceCount = total == 0 ? 0 : batchNo + (rowsInBatch > 0 || ordersInBatch > 0 ? 1 : 0);

        log.LogInformation(
            "PurgePlanningCompleted RunId={RunId} Ordini={Orders} Slice={Slices} " +
            "Oversized={Oversized} MaxRighe={MaxRows} MaxOrdini={MaxOrders} CollectiveAtomic={CollectiveAtomic}",
            run.RunId, total, sliceCount, oversized, run.MaxRowsPerBatch, run.MaxOrdersPerBatch,
            run.Strategy == RetentionStrategy.Collective);

        if (oversized > 0)
        {
            log.LogWarning(
                "RunId={RunId}: {Count} aggregati oversized. Per i Collective l'intero " +
                "aggregato resta comunque nella stessa transazione.", run.RunId, oversized);
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

        Guid? lastOrderHistoryId = null;
        while (true)
        {
            var pageRead = false;
            await using (var read = sql.Command(conn, null, RetentionSql.ReadOrphansForPlanningPage,
                            SqlParam.Of("@RunId", run.RunId),
                            SqlParam.Typed("@PageSize", _flushEvery, SqlDbType.Int),
                            SqlParam.Typed("@LastOrderHistoryId", lastOrderHistoryId, SqlDbType.UniqueIdentifier)))
            await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    pageRead = true;
                    var id = reader.GetGuid(0);
                    if (inBatch >= run.MaxOrdersPerBatch) { batchNo++; inBatch = 0; }
                    table.Rows.Add(id, batchNo);
                    inBatch++;
                    total++;
                    lastOrderHistoryId = id;
                }
            }

            if (table.Rows.Count >= _flushEvery)
            {
                await FlushOrphansAsync(conn, table, ct).ConfigureAwait(false);
                table.Clear();
            }

            if (!pageRead)
                break;
        }

        if (table.Rows.Count > 0)
            await FlushOrphansAsync(conn, table, ct).ConfigureAwait(false);

        await using (var apply = sql.Command(conn, null, RetentionSql.ApplyOrphanAssignments, SqlParam.Of("@RunId", run.RunId)))
            await apply.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using (var init = sql.Command(conn, null, RetentionSql.InitializeOrphanBatchProgress, SqlParam.Of("@RunId", run.RunId)))
            await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var slices = total == 0 ? 0 : batchNo + 1;
        log.LogInformation("PurgeOrphanPlanningCompleted RunId={RunId} Storici={Count} Slice={Slices}",
            run.RunId, total, slices);
        return slices;
    }

    private static async Task FlushOrphansAsync(SqlConnection conn, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#assignOrphan" };
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
        return t;
    }

    private static async Task FlushAsync(SqlConnection conn, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#assign" };
        bulk.ColumnMappings.Add("OrderId", "OrderId");
        bulk.ColumnMappings.Add("BatchNo", "BatchNo");
        bulk.ColumnMappings.Add("IsOversized", "IsOversized");
        bulk.ColumnMappings.Add("CollectiveOrderId", "CollectiveOrderId");
        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }
}
