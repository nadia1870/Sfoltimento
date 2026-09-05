using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class ExecutingPhase(
    IBatchExecutionCoordinator coordinator,
    IPurgeStrategyResolver strategyResolver) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Executing };

    public RunPhase Phase => RunPhase.Executing;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var result = await coordinator.ExecuteAsync(run, ct).ConfigureAwait(false);

        if (!result.Completed)
            return PhaseResult.Stay();

        var strategy = strategyResolver.Resolve(run.Strategy);

        return strategy.RequiresCollectiveTail
            ? PhaseResult.Next(RunPhase.CollectiveTail)
            : PhaseResult.Complete();
    }
}
