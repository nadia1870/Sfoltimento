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
public sealed class BatchPlanner(
    SqlExecutor sql,
    PurgeStrategyResolver strategyResolver,
    ILogger<BatchPlanner> log)
{
    private const int FlushEvery = 50_000;

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

            if (buffer.Rows.Count >= FlushEvery)
            {
                await FlushAsync(conn, buffer, ct).ConfigureAwait(false);
                buffer = NewTable();
            }
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

            if (buffer.Rows.Count >= FlushEvery)
            {
                await FlushAsync(conn, buffer, ct).ConfigureAwait(false);
                buffer = NewTable();
            }
        }

        await using (var read = sql.Command(conn, null,
                        RetentionSql.ReadCandidatesForPlanning, SqlParam.Of("@RunId", run.RunId)))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
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
            }
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

        await using (var read = sql.Command(conn, null, RetentionSql.ReadOrphansForPlanning, SqlParam.Of("@RunId", run.RunId)))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (inBatch >= run.MaxOrdersPerBatch) { batchNo++; inBatch = 0; }
                table.Rows.Add(reader.GetGuid(0), batchNo);
                inBatch++;
                total++;
                if (table.Rows.Count >= FlushEvery)
                {
                    await FlushOrphansAsync(conn, table, ct).ConfigureAwait(false);
                    table.Clear();
                }
            }
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
