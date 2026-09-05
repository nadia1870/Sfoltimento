using Microsoft.Extensions.Logging;
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
    ILogger<RetentionOrchestrator> log)
{
    private readonly IReadOnlyDictionary<RunPhase, IPurgePhase> _phases = BuildPhaseMap(phases);

    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        var run = await store.LoadAsync(runId, ct).ConfigureAwait(false);
        var strategy = strategyResolver.Resolve(run.Strategy);

        log.LogInformation(
            "PurgeRunStarted RunId={RunId} Strategy={Strategy} DryRun={DryRun} " +
            "AnchorMode={Anchor} Cutoff={Cutoff:yyyy-MM-dd}",
            runId, run.Strategy, run.DryRun, run.AnchorMode, strategy.CutoffOf(run));

        if (run.Phase is RunPhase.Completed or RunPhase.Failed or RunPhase.Aborted)
        {
            log.LogWarning(
                "RunId={RunId} e' gia' in stato {Phase}: nessuna azione.", runId, run.Phase);
            return;
        }

        try
        {
            while (true)
            {
                var phase = ResolvePhase(run.Phase);
                var result = await phase.ExecuteAsync(run, ct).ConfigureAwait(false);

                if (result.NextPhase is { } nextPhase)
                    await TransitionAsync(run, nextPhase, result.Error, ct).ConfigureAwait(false);

                if (result.Stop)
                    return;

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
            log.LogInformation(
                "PurgeRunAborted RunId={RunId} Phase={Phase} — ripresa dal checkpoint",
                runId, run.Phase);
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
            foreach (var handled in phase.HandledPhases)
            {
                if (!map.TryAdd(handled, phase))
                {
                    throw new InvalidOperationException(
                        $"Più IPurgePhase gestiscono la RunPhase '{handled}'.");
                }
            }
        }

        return map;
    }
}
