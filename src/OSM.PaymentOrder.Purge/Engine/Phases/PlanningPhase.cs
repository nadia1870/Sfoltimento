using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class PlanningPhase(
    BatchPlanner planner,
    DryRunReporter dryRunReporter,
    IOptions<PurgeOptions> options,
    ILogger<PlanningPhase> log) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Planning };

    private readonly PurgeOptions _options = options.Value;

    public RunPhase Phase => RunPhase.Planning;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        await planner.PlanAsync(run, ct).ConfigureAwait(false);

        // Il report non serve solo al dry-run: Purge.vDryRunVsActual confronta
        // previsto ed effettivo a parita' di RunId, e il RunId di un dry-run
        // non e' mai quello del run che cancella davvero. Senza baseline sul
        // run reale la view e' vuota per costruzione, e lo scostamento fra
        // cio' che era stato approvato e cio' che e' stato cancellato non e'
        // verificabile a posteriori.
        //
        // Il costo e' una passata di conteggi sullo staging, una sola volta
        // per run, prima della parte distruttiva.
        var report = run.DryRun || _options.AuditBaselineEnabled
            ? await dryRunReporter.ProduceAsync(run, ct).ConfigureAwait(false)
            : null;

        if (report is not null)
        {
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
        }

        if (!run.DryRun)
            return PhaseResult.Next(RunPhase.Executing);

        log.LogInformation(
            "PurgeDryRunReport RunId={RunId}{NewLine}{Report}",
            run.RunId, Environment.NewLine, report!.ToText());

        return PhaseResult.Complete();
    }
}
