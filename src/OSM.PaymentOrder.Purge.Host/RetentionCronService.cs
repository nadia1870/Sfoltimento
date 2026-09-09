using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Host;

/// <summary>
/// Cronjob dello sfoltimento per retention.
///
/// Il cron decide QUANDO iniziare; la finestra operativa decide FINO A QUANDO
/// proseguire. Un run interrotto dalla fine della finestra non riparte da capo
/// la notte successiva: riprende dalla prima slice non completata, con il set
/// di candidati congelato (§7.2).
///
/// Le strategie vengono eseguite in sequenza, non in parallelo: condividono le
/// stesse tabelle e la concorrenza fra loro produrrebbe solo deadlock.
/// </summary>
public sealed class RetentionCronService(
    PurgeHousekeeping housekeeping,
    SchemaVerifier schemaVerifier,
    RetentionOrchestrator orchestrator,
    PurgeRunStore store,
    IOptions<PurgeOptions> options,
    TimeProvider clock,
    PurgeExecutionLock executionLock,
    PurgeWindowGuard windowGuard,
    ILogger<RetentionCronService> log) : BackgroundService
{
    private readonly PurgeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fallire all'avvio del servizio e' preferibile a fallire alle due di
        // notte dentro la fase di validazione.
        await schemaVerifier.EnsureAsync(stoppingToken).ConfigureAwait(false);
        var schedule = CronExpression.Parse(_options.CronExpression);

        // Lo stesso fuso della finestra, non quello dell'host (D-16).
        // Erano rimasti disallineati: la finestra rispettava il fuso
        // dichiarato, la pianificazione no, e su un host in UTC le due cose
        // divergevano di due ore. Il servizio si sarebbe svegliato alle 03:00
        // italiane per lavorare in una finestra che chiude alle 05:00, con due
        // ore di lavoro perse ogni notte e nessun errore da nessuna parte.
        var tz = _options.TimeZone;

        log.LogInformation(
            "Cronjob di retention avviato. Cron={Cron} Fuso={Fuso} Finestra={Start}-{End} DryRun={DryRun}",
            _options.CronExpression, tz.Id, _options.WindowStart, _options.WindowEnd, _options.DryRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Un run sospeso ha priorita' sul prossimo risveglio: se la finestra
            // e' aperta si riprende subito, senza attendere il cron.
            if (_options.IsWithinWindow(_options.Now(clock))
                && await HasResumableRunAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunAllStrategiesAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            var now = _options.Now(clock);
            var next = schedule.GetNextOccurrence(now, tz);
            if (next is null)
            {
                log.LogError("Espressione cron '{Cron}' senza occorrenze future.",
                    _options.CronExpression);
                return;
            }

            var delay = next.Value - now;
            log.LogInformation("Prossimo risveglio {Next:yyyy-MM-dd HH:mm} (fra {Delay})",
                next.Value, delay);

            try { await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            await RunAllStrategiesAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> HasResumableRunAsync(CancellationToken ct)
    {
        foreach (var strategy in _options.Strategies)
            if (await store.FindResumableAsync(strategy, ct).ConfigureAwait(false) is not null)
                return true;
        return false;
    }

    private async Task RunAllStrategiesAsync(CancellationToken ct)
    {
        // Controllo anticipato: aprire la finestra fuori orario produrrebbe una
        // scadenza gia' scaduta, e il primo await fallirebbe con una
        // cancellazione al posto di una riga di log comprensibile.
        if (!_options.IsWithinWindow(_options.Now(clock)))
        {
            log.LogInformation("Fuori dalla finestra operativa: ciclo saltato.");
            return;
        }

        await using var lease = await executionLock.TryAcquireAsync(ct).ConfigureAwait(false);
        if (lease is null)
        {
            log.LogWarning("Un'altra istanza del purge è già in esecuzione: ciclo saltato.");
            return;
        }

        // Le fasi lunghe — selezione, espansione, validazione, planning — non
        // controllano la finestra da se': e' questo token a fermarle.
        using var window = windowGuard.Open(ct);

        var strategies = new List<RetentionStrategy>(_options.Strategies);

        // Gli abbandoni hanno soglia e stati propri: si eseguono solo se
        // abilitati, perche' la durata dipende da una decisione ancora aperta.
        if (_options.AbandonedEnabled && !strategies.Contains(RetentionStrategy.Abandoned))
            strategies.Add(RetentionStrategy.Abandoned);

        foreach (var strategy in strategies)
        {
            if (ct.IsCancellationRequested) return;

            if (!_options.IsWithinWindow(_options.Now(clock)))
            {
                log.LogInformation("Finestra chiusa: strategie residue rinviate.");
                return;
            }

            // Vedi Program: il lock di sessione puo' essere stato rilasciato da
            // una caduta di connessione senza che nessuno lo abbia notato.
            try
            {
                await lease.EnsureHeldAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                log.LogError(ex, "PurgeLeaseLost Strategy={Strategy}: ciclo interrotto.", strategy);
                return;
            }

            try
            {
                var runId = await store.FindResumableAsync(strategy, window.Token).ConfigureAwait(false)
                            ?? await store.CreateAsync(strategy, _options, _options.Now(clock), window.Token)
                                          .ConfigureAwait(false);

                await orchestrator.RunAsync(runId, window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (window.ClosedByWindow)
            {
                // A differenza di una strategia fallita, qui non si prosegue: le
                // successive lavorerebbero fuori finestra. Il run resta
                // riprendibile e riparte dal checkpoint alla prossima apertura.
                log.LogError(
                    "Strategia {Strategy} interrotta dalla chiusura della finestra: " +
                    "le strategie successive sono rinviate.", strategy);

                return;
            }
            catch (Exception ex)
            {
                // Una strategia fallita non deve impedire le altre.
                log.LogError(ex, "Strategia {Strategy} fallita, si prosegue con le successive.",
                    strategy);
            }
        }

        // A fine ciclo: lo staging dei run gia' chiusi non serve piu'.
        try
        {
            await housekeeping.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Housekeeping fallito, il ciclo prosegue.");
        }
    }
}
