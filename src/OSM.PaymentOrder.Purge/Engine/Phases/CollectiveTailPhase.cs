using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

/// <summary>Legacy recovery phase: i nuovi run non la raggiungono piu'.</summary>
public sealed class CollectiveTailPhase(CollectiveTailExecutor executor) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.CollectiveTail };

    public RunPhase Phase => RunPhase.CollectiveTail;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var completed = await executor.ExecuteAsync(run, ct).ConfigureAwait(false);
        return completed
            ? PhaseResult.Complete()
            : PhaseResult.Stay();
    }
}
