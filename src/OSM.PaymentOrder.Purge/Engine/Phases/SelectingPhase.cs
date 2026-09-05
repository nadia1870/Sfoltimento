using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class SelectingPhase(
    PurgeStrategyResolver strategyResolver,
    DryRunReporter dryRunReporter,
    ILogger<SelectingPhase> log) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Created, RunPhase.Selecting };

    public RunPhase Phase => RunPhase.Selecting;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var strategy = strategyResolver.Resolve(run.Strategy);
        var selected = await strategy.SelectAsync(run, ct).ConfigureAwait(false);

        if (selected == 0)
        {
            // Zero candidati e' un esito. In dry-run produciamo comunque il
            // report, cosi' "nessun risultato" non viene confuso con "non eseguito".
            if (run.DryRun)
                await dryRunReporter.ProduceAsync(run, ct).ConfigureAwait(false);

            log.LogInformation(
                "PurgeSelectionEmpty RunId={RunId} Strategy={Strategy} Candidati=0 " +
                "Cutoff={Cutoff:yyyy-MM-dd} — nessun ordine eleggibile.",
                run.RunId, run.Strategy, strategy.CutoffOf(run));

            return PhaseResult.Complete();
        }

        return PhaseResult.Next(RunPhase.Expanding);
    }
}
