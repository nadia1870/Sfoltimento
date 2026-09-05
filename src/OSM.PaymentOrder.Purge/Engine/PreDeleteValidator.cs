using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Fase Validating (§7.3). Con transazioni atomiche un'anomalia dati non
/// produce corruzione, ma fa fallire e abbandonare la slice: un problema
/// sistematico non intercettato qui si tradurrebbe in abbandono di massa.
/// </summary>
public sealed class PreDeleteValidator(
    SqlExecutor sql,
    PurgeStrategyResolver strategyResolver,
    ILogger<PreDeleteValidator> log)
{
    public async Task<ValidationReport> ValidateAsync(PurgeRun run, CancellationToken ct)
    {
        var report = new ValidationReport();
        var strategy = strategyResolver.Resolve(run.Strategy);
        var p = SqlParam.Of("@RunId", run.RunId);

        // V1 — caso C7: RefId verso un dettaglio corrente fuori dal set.
        foreach (var t in PurgeTopology.DetailHistoryTables)
        {
            ct.ThrowIfCancellationRequested();
            report.Add("V1", t.Name,
                await sql.ScalarAsync<long>(RetentionSql.ValidateCrossReferences(t), ct, p)
                         .ConfigureAwait(false));
        }

        // V2 — protezione modelli (C5).
        report.Add("V2", "Model",
            await sql.ScalarAsync<long>(RetentionSql.ValidateNoModelReference, ct, p)
                     .ConfigureAwait(false));

        // V3 — coerenza collettivi. Nella strategia Collective il legame e'
        // atteso: il controllo si applica solo alle altre strategie.
        if (!strategy.SkipCollectiveLinkValidation)
        {
            report.Add("V3", "CollectiveOrderGroupOrder",
                await sql.ScalarAsync<long>(RetentionSql.ValidateNoCollectiveLink, ct, p)
                         .ConfigureAwait(false));
        }

        // V4 — rivalidazione dello stato fra selezione ed esecuzione.
        report.Add("V4", "Order",
            await sql.ScalarAsync<long>(RetentionSql.ValidateStatusUnchanged(strategy.UsesAbandonedDeletes), ct, p)
                     .ConfigureAwait(false));

        // V5 — copertura storici.
        report.Add("V5", "OrderHistory",
            await sql.ScalarAsync<long>(RetentionSql.ValidateHistoryCoverage, ct, p)
                     .ConfigureAwait(false));

        await PersistAsync(run, report, ct).ConfigureAwait(false);

        if (report.HasBlockingIssues)
        {
            log.LogError("PurgeValidationFailed RunId={RunId} Regole={Rules} Righe={Rows}",
                run.RunId, report.FailedRules, report.TotalAffected);
        }
        else
        {
            log.LogInformation("PurgeValidationPassed RunId={RunId}", run.RunId);
        }

        return report;
    }

    private async Task PersistAsync(PurgeRun run, ValidationReport report, CancellationToken ct)
    {
        const string insert = """
            INSERT INTO Purge.ValidationFinding (RunId, RuleId, TableName, AffectedCount, DetectedOn)
            VALUES (@RunId, @RuleId, @Table, @Count, SYSDATETIMEOFFSET());
            """;

        foreach (var f in report.Findings)
        {
            await sql.ExecuteAsync(insert, ct,
                SqlParam.Of("@RunId", run.RunId),
                SqlParam.Of("@RuleId", f.RuleId),
                SqlParam.Of("@Table", f.Table),
                SqlParam.Of("@Count", f.AffectedCount)).ConfigureAwait(false);
        }
    }
}
