using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Sfoltimento delle tabelle di controllo del purge.
///
/// Il motore produce staging in proporzione a cio' che cancella: una riga in
/// RunCandidateOrder per ordine e una in RunCandidateOrderHistory per revisione.
/// Su volumi reali quello staging puo' superare in dimensione i dati che ha
/// rimosso, quindi va sfoltito a sua volta.
///
/// Tre regole distinte:
///   - staging dei run conclusi senza incidenti  -> finestra breve
///   - staging dei run falliti o con slice abbandonate -> finestra lunga,
///     perche' e' l'unico appiglio per l'analisi post-mortem
///   - PurgeRun, PurgeAudit, DryRunReport, ValidationFinding, RunBatchProgress
///     -> mai toccati: sono la traccia, e sono piccoli
///
/// L'operazione non lascia traccia in Purge.PurgeAudit: registrare lo
/// sfoltimento dello staging genererebbe a sua volta righe da sfoltire.
/// Un solo log di riepilogo.
/// </summary>
public sealed class PurgeHousekeeping(
    ISqlExecutor sql,
    IOptions<PurgeOptions> options,
    TimeProvider clock,
    ILogger<PurgeHousekeeping> log)
{
    private sealed record RunToClean(Guid RunId, string Strategy, string Phase);

    /// <summary>Colonne di RetentionSql.StagingFootprint. NULL diventa zero.</summary>
    private sealed record Footprint(int RunConStaging, long Ordini, long Storici);

    private readonly PurgeOptions _options = options.Value;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!_options.HousekeepingEnabled)
        {
            log.LogDebug("Housekeeping disabilitato da configurazione.");
            return 0;
        }

        var now = _options.Now(clock);
        var completedCutoff = now.AddDays(-_options.StagingRetentionDays);
        var failedCutoff = now.AddDays(-_options.FailedStagingRetentionDays);

        var runs = await sql.QueryAsync<RunToClean>(RetentionSql.SelectRunsToClean, ct,
            SqlParam.Of("@MaxRuns", _options.HousekeepingMaxRunsPerCycle),
            SqlParam.Of("@CompletedCutoff", completedCutoff),
            SqlParam.Of("@FailedCutoff", failedCutoff)).ConfigureAwait(false);

        if (runs.Count == 0)
        {
            log.LogDebug("Housekeeping: nessun run con staging eliminabile.");
            return 0;
        }

        long totalRows = 0;
        var cleaned = 0;

        foreach (var run in runs)
        {
            if (ct.IsCancellationRequested) break;

            // Rispetta la finestra operativa come tutto il resto: l'housekeeping
            // non e' urgente e non deve rubare risorse all'OLTP.
            if (!_options.IsWithinWindow(_options.Now(clock)))
            {
                log.LogInformation("Housekeeping interrotto: finestra chiusa. Ripulisi={Cleaned}", cleaned);
                break;
            }

            try
            {
                totalRows += await CleanRunAsync(run.RunId, ct).ConfigureAwait(false);
                cleaned++;
            }
            catch (Exception ex)
            {
                // Un run problematico non deve bloccare la pulizia degli altri.
                log.LogError(ex, "Housekeeping fallito per RunId={RunId}, si prosegue.", run.RunId);
            }
        }

        log.LogInformation(
            "PurgeHousekeepingCompleted RunRipuliti={Cleaned}/{Total} RigheRimosse={Rows} " +
            "SogliaConclusi={CompletedCutoff:yyyy-MM-dd} SogliaFalliti={FailedCutoff:yyyy-MM-dd}",
            cleaned, runs.Count, totalRows, completedCutoff, failedCutoff);

        await LogFootprintAsync(ct).ConfigureAwait(false);
        return cleaned;
    }

    private async Task<long> CleanRunAsync(Guid runId, CancellationToken ct)
    {
        long rows = 0;

        foreach (var (table, statement) in RetentionSql.HousekeepingStatements)
        {
            while (!ct.IsCancellationRequested)
            {
                var deleted = await sql.ExecuteAsync(statement, ct,
                    SqlParam.Of("@RunId", runId),
                    SqlParam.Of("@BatchSize", _options.HousekeepingBatchSize)).ConfigureAwait(false);

                rows += deleted;
                if (deleted < _options.HousekeepingBatchSize) break;

                // Stessa cautela applicata alle slice: rilasciare i lock fra un
                // batch e l'altro.
                await Task.Delay(_options.InterSliceDelay, clock, ct).ConfigureAwait(false);
            }

            log.LogDebug("Housekeeping RunId={RunId} Tabella={Table} completata.", runId, table);
        }

        // Marcatore scritto solo dopo lo svuotamento di tutte e tre le tabelle:
        // anticiparlo lascerebbe righe residue invisibili ai cicli successivi.
        await sql.ExecuteAsync(RetentionSql.MarkStagingPurged, ct,
            SqlParam.Of("@RunId", runId)).ConfigureAwait(false);

        return rows;
    }

    private async Task LogFootprintAsync(CancellationToken ct)
    {
        try
        {
            var footprint = await sql.QueryAsync<Footprint>(RetentionSql.StagingFootprint, ct)
                .ConfigureAwait(false);

            if (footprint.Count == 0) return;

            var f = footprint[0];
            log.LogInformation(
                "StagingResiduo RunNonRipuliti={Runs} Ordini={Ordini} Storici={Storici}",
                f.RunConStaging, f.Ordini, f.Storici);
        }
        catch (Exception ex)
        {
            // Metrica informativa: un errore qui non deve far fallire nulla.
            log.LogDebug(ex, "Impossibile calcolare lo staging residuo.");
        }
    }
}
