using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class PlanningPhase(
    BatchPlanner planner,
    DryRunReporter dryRunReporter,
    ILogger<PlanningPhase> log) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Planning };

    public RunPhase Phase => RunPhase.Planning;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        await planner.PlanAsync(run, ct).ConfigureAwait(false);

        if (!run.DryRun)
            return PhaseResult.Next(RunPhase.Executing);

        var report = await dryRunReporter.ProduceAsync(run, ct).ConfigureAwait(false);

        if (report.ExceedsRowBudget(run.MaxRowsPerBatch))
        {
            log.LogWarning(
                "RunId={RunId}: slice massima {Max} righe oltre il tetto {Budget}. " +
                "Rivedere MaxRowsPerBatch prima dell'esecuzione reale.",
                run.RunId, report.MaxRowsPerSlice, run.MaxRowsPerBatch);
        }

        if (report.UnassignedOrders > 0)
        {
            log.LogError(
                "RunId={RunId}: {Count} ordini senza BatchNo, resterebbero fuori dal run.",
                run.RunId, report.UnassignedOrders);
        }

        log.LogInformation(
            "PurgeRunCompleted RunId={RunId} Modalita=DryRun{NewLine}{Report}",
            run.RunId, Environment.NewLine, report.ToText());

        return PhaseResult.Complete();
    }
}
