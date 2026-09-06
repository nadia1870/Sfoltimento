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
        return result.Completed
            ? PhaseResult.Complete()
            : PhaseResult.Stay();
    }
}
