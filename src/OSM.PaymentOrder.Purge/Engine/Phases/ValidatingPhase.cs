using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

public sealed class ValidatingPhase(PreDeleteValidator validator) : IPurgePhase
{
    private static readonly IReadOnlySet<RunPhase> Supported =
        new HashSet<RunPhase> { RunPhase.Validating };

    public RunPhase Phase => RunPhase.Validating;
    public IReadOnlySet<RunPhase> HandledPhases => Supported;

    public async Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        var findings = await validator.ValidateAsync(run, ct).ConfigureAwait(false);
        if (findings.HasBlockingIssues)
            return PhaseResult.Fail($"Validazione fallita: {findings.FailedRules}");

        return PhaseResult.Next(RunPhase.Planning);
    }
}
