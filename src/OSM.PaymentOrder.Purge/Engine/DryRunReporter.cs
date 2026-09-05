using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Report del dry-run (§7.4). I conteggi rispecchiano esattamente i join degli
/// statement di delete: se il conteggio e' sbagliato, lo sarebbe anche la
/// cancellazione. Il dry-run verifica quindi anche le query, non solo i volumi.
/// </summary>
public sealed class DryRunReporter(SqlExecutor sql, ILogger<DryRunReporter> log)
{
    public async Task<DryRunReport> ProduceAsync(PurgeRun run, CancellationToken ct)
    {
        var report = new DryRunReport { RunId = run.RunId };
        var p = SqlParam.Of("@RunId", run.RunId);

        foreach (var t in PurgeTopology.DetailHistoryTables)
        {
            ct.ThrowIfCancellationRequested();
            report.Add(t.Name, await sql.ScalarAsync<long>(RetentionSql.CountDetailHistory(t), ct, p)
                                        .ConfigureAwait(false));
        }

        report.Add("OrderHistory",
            await sql.ScalarAsync<long>(RetentionSql.CountOrderHistory, ct, p).ConfigureAwait(false));

        foreach (var table in PurgeTopology.DetailTables)
        {
            ct.ThrowIfCancellationRequested();
            report.Add(table, await sql.ScalarAsync<long>(RetentionSql.CountDetail(table), ct, p)
                                       .ConfigureAwait(false));
        }

        report.Add("Order", await sql.ScalarAsync<long>(RetentionSql.CountOrder, ct, p)
                                     .ConfigureAwait(false));

        if (run.Strategy == RetentionStrategy.Collective)
        {
            foreach (var table in PurgeTopology.CollectiveTailTables)
            {
                ct.ThrowIfCancellationRequested();
                report.Add(table, await sql.ScalarAsync<long>(RetentionSql.CountCollectiveTail(table), ct, p)
                                           .ConfigureAwait(false));
            }
        }

        report.UnassignedOrders = await sql.ScalarAsync<long>(RetentionSql.CountUnassigned, ct, p)
                                           .ConfigureAwait(false);

        // In dry-run RunBatchProgress non viene popolata: le statistiche
        // vengono ricavate direttamente dalle assegnazioni prodotte dal planner.
        var sliceStatisticsSql = run.Strategy == RetentionStrategy.OrphanHistory
            ? RetentionSql.DryRunOrphanSliceStatistics
            : RetentionSql.DryRunSliceStatistics;

        var stats = await sql.QueryAsync(sliceStatisticsSql, r => new
        {
            SliceCount = r.IsDBNull(0) ? 0 : r.GetInt32(0),
            MinRows = r.IsDBNull(1) ? 0 : r.GetInt32(1),
            MaxRows = r.IsDBNull(2) ? 0 : r.GetInt32(2),
            AvgRows = r.IsDBNull(3) ? 0 : r.GetInt32(3),
            Oversized = r.IsDBNull(4) ? 0 : r.GetInt32(4)
        }, ct, p).ConfigureAwait(false);

        if (stats.Count > 0)
        {
            report.SliceCount = stats[0].SliceCount;
            report.MinRowsPerSlice = stats[0].MinRows;
            report.MaxRowsPerSlice = stats[0].MaxRows;
            report.AvgRowsPerSlice = stats[0].AvgRows;
            report.OversizedCount = stats[0].Oversized;
        }

        await PersistAsync(report, ct).ConfigureAwait(false);

        log.LogInformation(
            "PurgeDryRunReport RunId={RunId} Righe={Rows} Ordini={Orders} Slice={Slices} " +
            "Oversized={Oversized} MinMax={Min}/{Max} NonAssegnati={Unassigned}",
            run.RunId, report.TotalRows, report.OrderCount, report.SliceCount,
            report.OversizedCount, report.MinRowsPerSlice, report.MaxRowsPerSlice,
            report.UnassignedOrders);

        return report;
    }

    private async Task PersistAsync(DryRunReport report, CancellationToken ct)
    {
        const string insert = """
            INSERT INTO Purge.DryRunReport (RunId, TableName, RowCountEstimate, ProducedOn)
            VALUES (@RunId, @Table, @Count, @On);
            """;

        foreach (var (table, count) in report.Lines)
        {
            await sql.ExecuteAsync(insert, ct,
                SqlParam.Of("@RunId", report.RunId),
                SqlParam.Of("@Table", table),
                SqlParam.Of("@Count", count),
                SqlParam.Of("@On", report.ProducedOn)).ConfigureAwait(false);
        }
    }
}
