using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class ExecutingPhase(
    IBatchExecutionCoordinator coordinator,
    PurgeRunStore store) : IPurgePhase
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

        // Il conteggio arriva dal database, non da result.AbandonedSlices.
        // Quel contatore vale solo per l'invocazione corrente: un run che
        // abbandona due slice la prima notte e ne completa il resto la seconda
        // chiuderebbe con zero abbandoni e perderebbe l'informazione.
        var abandoned = await store.CountAbandonedSlicesAsync(run.RunId, ct).ConfigureAwait(false);

        return abandoned == 0
            ? PhaseResult.Complete()
            : PhaseResult.CompleteWithErrors(
                $"{abandoned} slice abbandonate: gli aggregati corrispondenti non sono " +
                "stati cancellati e restano a database.");
    }
}
