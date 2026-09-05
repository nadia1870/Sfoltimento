using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class ExpandingPhase(PurgeStrategyResolver strategyResolver) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Expanding };

    public RunPhase Phase => RunPhase.Expanding;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var strategy = strategyResolver.Resolve(run.Strategy);
        await strategy.ExpandAsync(run, ct).ConfigureAwait(false);
        return PhaseResult.Next(RunPhase.Validating);
    }
}
