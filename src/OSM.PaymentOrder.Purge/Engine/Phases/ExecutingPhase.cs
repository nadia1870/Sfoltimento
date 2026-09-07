using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class ExecutingPhase(
    IBatchExecutionCoordinator coordinator) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Executing };

    public RunPhase Phase => RunPhase.Executing;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var result = await coordinator.ExecuteAsync(run, ct).ConfigureAwait(false);

        // Completed = false significa finestra chiusa o lavoro sospeso: la fase
        // resta la stessa e il run riprendera' dal checkpoint.
        if (!result.Completed)
            return PhaseResult.Stay();

        // AbandonedTotal, non AbandonedSlices: il secondo conta solo questa
        // sessione, e un run che abbandona una notte e completa quella dopo
        // chiuderebbe come pulito.
        return result.AbandonedTotal == 0
            ? PhaseResult.Complete()
            : PhaseResult.CompleteWithErrors(
                $"{result.AbandonedTotal} slice abbandonate: gli aggregati " +
                "corrispondenti non sono stati cancellati e restano a database.");
    }
}
