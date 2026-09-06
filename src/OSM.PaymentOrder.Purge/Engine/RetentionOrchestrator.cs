using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine.Phases;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Macchina a stati del run (§7.2).
///
/// La sequenza non e' piu' codificata in una catena di if. Ogni stato e'
/// gestito da un IPurgePhase e l'orchestratore applica sempre la stessa regola:
/// esegui la fase -> persisti la transizione -> aggiorna il modello in memoria.
/// In caso di eccezione la fase corrente non viene avanzata.
/// </summary>
public sealed class RetentionOrchestrator(
    PurgeRunStore store,
    IEnumerable<IPurgePhase> phases,
    PurgeStrategyResolver strategyResolver,
    IOptions<PurgeOptions> options,
    ILogger<RetentionOrchestrator> log)
{
    private readonly IReadOnlyDictionary<RunPhase, IPurgePhase> _phases = BuildPhaseMap(phases);
    private readonly PurgeOptions _options = options.Value;

    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        var run = await store.LoadAsync(runId, ct).ConfigureAwait(false);
        var strategy = strategyResolver.Resolve(run.Strategy);

        log.LogInformation(
            "PurgeRunStarted RunId={RunId} Strategy={Strategy} DryRun={DryRun} " +
            "AnchorMode={Anchor} Cutoff={Cutoff:yyyy-MM-dd}",
            runId, run.Strategy, run.DryRun, run.AnchorMode, strategy.CutoffOf(run));

        if (RunPhases.IsTerminal(run.Phase))
        {
            log.LogWarning(
                "RunId={RunId} e' gia' in stato {Phase}: nessuna azione.", runId, run.Phase);
            return;
        }

        // Un guasto non chiude il run, quindi un guasto stabile lo farebbe
        // riprendere ogni notte con lo stesso esito. Oltre la soglia si smette
        // di riprovare e si chiede a qualcuno di guardarlo.
        if (run.InterruptionCount >= _options.MaxRunInterruptions)
        {
            var motivo =
                $"Interrotto {run.InterruptionCount} volte in fase {run.Phase}: " +
                "oltre il limite di riprese, richiede analisi.";

            log.LogError("PurgeRunExhausted RunId={RunId} Phase={Phase} Interruzioni={Count}",
                runId, run.Phase, run.InterruptionCount);

            await store.SetPhaseAsync(runId, RunPhase.Failed, ct, motivo).ConfigureAwait(false);
            return;
        }

        try
        {
            while (true)
            {
                var phase = ResolvePhase(run.Phase);

                // A phase may handle more than one persisted state for backward
                // compatibility (currently Selecting also handles Created).
                // The persisted state must nevertheless become the phase
                // checkpoint before any work is executed. Otherwise an exception
                // in Selecting leaves the run in Created, so the next execution
                // cannot distinguish "not started" from "interrupted while
                // selecting" and the lifecycle checkpoint is wrong.
                if (run.Phase != phase.Phase)
                    await TransitionAsync(run, phase.Phase, null, ct).ConfigureAwait(false);

                var result = await phase.ExecuteAsync(run, ct).ConfigureAwait(false);

                if (result.NextPhase is { } nextPhase)
                    await TransitionAsync(run, nextPhase, result.Error, ct).ConfigureAwait(false);

                if (result.Stop)
                {
                    // Unico punto in cui si dichiara concluso un run: le fasi
                    // sanno cosa hanno fatto, non se il run e' finito.
                    if (run.Phase == RunPhase.Completed)
                        log.LogInformation(
                            "PurgeRunCompleted RunId={RunId} Strategy={Strategy} DryRun={DryRun}",
                            runId, run.Strategy, run.DryRun);

                    return;
                }

                // Stay non dovrebbe mai essere usato con Stop=false, ma il
                // contratto lo rende esplicito per evitare loop accidentali.
                if (result.NextPhase is null)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // Non cambiamo fase: la fase corrente e' esattamente il checkpoint
            // logico da cui riprendere. Durante Executing i checkpoint di slice
            // garantiscono inoltre la ripresa dalla prima slice non completata.
            //
            // Non conta come interruzione: la chiusura della finestra operativa
            // e' il funzionamento previsto, non un guasto.
            log.LogInformation(
                "PurgeRunAborted RunId={RunId} Phase={Phase} — ripresa dal checkpoint",
                runId, run.Phase);
            throw;
        }
        catch (Exception ex) when (IsInfrastructure(ex))
        {
            // Stessa scelta della cancellazione, per la stessa ragione: la fase
            // corrente e' il checkpoint. Marcare Failed qui significherebbe
            // buttare un set di candidati congelato e ore di lavoro gia' fatto
            // perche' la rete e' caduta per un secondo.
            await store.RecordInterruptionAsync(
                runId, ex.Message, CancellationToken.None).ConfigureAwait(false);

            log.LogWarning(ex,
                "PurgeRunInterrupted RunId={RunId} Phase={Phase} Interruzione={Count} — " +
                "il run resta riprendibile",
                runId, run.Phase, run.InterruptionCount + 1);

            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "PurgeRunFailed RunId={RunId} Phase={Phase}", runId, run.Phase);
            await store.SetPhaseAsync(
                runId, RunPhase.Failed, CancellationToken.None, ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Guasto dell'ambiente o difetto del programma.
    ///
    /// La distinzione non e' accademica: un difetto va fermato e guardato, un
    /// guasto va ripreso. Confonderli in un unico Failed terminale significa
    /// che una disconnessione alle due di notte distrugge il run, e che un
    /// difetto sistematico viene invece riprovato per sempre.
    ///
    /// Nel dubbio si classifica come difetto: fermarsi e chiedere aiuto e'
    /// meno grave che riprovare all'infinito una cancellazione sbagliata.
    /// </summary>
    private static bool IsInfrastructure(Exception ex) => ex switch
    {
        SqlException sql => SqlErrors.IsTransient(sql),
        TimeoutException => true,
        IOException => true,
        _ => false
    };

    private async Task TransitionAsync(
        PurgeRun run,
        RunPhase nextPhase,
        string? error,
        CancellationToken ct)
    {
        await store.SetPhaseAsync(run.RunId, nextPhase, ct, error).ConfigureAwait(false);
        run.Phase = nextPhase;
    }

    private IPurgePhase ResolvePhase(RunPhase current)
    {
        if (_phases.TryGetValue(current, out var phase))
            return phase;

        throw new InvalidOperationException(
            $"RunPhase '{current}' non gestita da alcun IPurgePhase.");
    }

    private static IReadOnlyDictionary<RunPhase, IPurgePhase> BuildPhaseMap(
        IEnumerable<IPurgePhase> phases)
    {
        var map = new Dictionary<RunPhase, IPurgePhase>();

        foreach (var phase in phases)
        {
            foreach (var handledPhase in phase.HandledPhases)
            {
                if (!map.TryAdd(handledPhase, phase))
                {
                    throw new InvalidOperationException(
                        $"La RunPhase '{handledPhase}' è gestita da più IPurgePhase.");
                }
            }
        }

        return map;
    }
}
