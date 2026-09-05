using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Observability;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class ExecutingPhase(
    PurgeRunStore store,
    SliceExecutor executor,
    PurgeStrategyResolver strategyResolver,
    PurgeMetrics metrics,
    IOptions<PurgeOptions> options,
    ILogger<ExecutingPhase> log) : IPurgePhase
{
    private readonly PurgeOptions _options = options.Value;
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Executing };

    public RunPhase Phase => RunPhase.Executing;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var completed = 0;
        var abandoned = 0;
        long totalRows = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!_options.IsWithinWindow(DateTimeOffset.Now))
            {
                log.LogInformation(
                    "Fine finestra operativa: RunId={RunId} sospeso, slice completate={Completed}",
                    run.RunId, completed);
                return PhaseResult.Stay();
            }

            var slice = await store.NextPendingSliceAsync(run.RunId, ct).ConfigureAwait(false);
            if (slice is null)
                break;

            var result = await executor.ExecuteAsync(run, slice, ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SliceOutcome.Completed:
                    completed++;
                    totalRows += result.RowsDeleted;
                    break;

                case SliceOutcome.Retryable when slice.AttemptCount + 1 < _options.MaxSliceAttempts:
                    await store.RecordAttemptAsync(
                        run.RunId, slice.BatchNo, result.Reason, ct).ConfigureAwait(false);
                    await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
                    break;

                default:
                    // Un singolo aggregato problematico non deve bloccare
                    // l'intero sfoltimento.
                    await store.AbandonSliceAsync(
                        run.RunId, slice.BatchNo, result.Reason, ct).ConfigureAwait(false);
                    metrics.SliceAbandoned(result.Reason ?? "unknown");
                    abandoned++;
                    break;
            }

            await Task.Delay(_options.InterSliceDelay, ct).ConfigureAwait(false);
        }

        log.LogInformation(
            "PurgeRunCompleted RunId={RunId} Righe={Rows} SliceCompletate={Completed} " +
            "SliceAbbandonate={Abandoned}",
            run.RunId, totalRows, completed, abandoned);

        if (abandoned > 0)
        {
            log.LogWarning(
                "RunId={RunId}: {Count} slice abbandonate, richiedono analisi.",
                run.RunId, abandoned);
        }

        var strategy = strategyResolver.Resolve(run.Strategy);

        // Completed e' terminale: va restituito con Complete(), non con Next().
        // Next() lascia Stop = false, quindi l'orchestratore proseguirebbe il
        // ciclo e cercherebbe un handler per RunPhase.Completed, che non esiste.
        return strategy.RequiresCollectiveTail
            ? PhaseResult.Next(RunPhase.CollectiveTail)
            : PhaseResult.Complete();
    }
}
