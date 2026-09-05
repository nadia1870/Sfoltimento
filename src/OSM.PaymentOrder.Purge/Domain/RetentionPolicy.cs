namespace OSM.PaymentOrder.Purge.Domain;

/// <summary>
/// Regole che determinano quando un dato diventa eleggibile.
/// Non contiene la logica di cancellazione: quella appartiene alla Strategy.
/// </summary>
public sealed record RetentionPolicy(
    int RetentionYears,
    RetentionAnchorMode AnchorMode,
    int AbandonedRetentionMonths)
{
    public DateTime ComputeRetentionCutoff(DateTimeOffset reference) => AnchorMode switch
    {
        RetentionAnchorMode.RollingDate => reference.Date.AddYears(-RetentionYears),
        RetentionAnchorMode.FiscalYearEnd => new DateTime(reference.Year - RetentionYears, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(AnchorMode))
    };

    public DateTime ComputeAbandonedCutoff(DateTimeOffset reference) =>
        reference.Date.AddMonths(-AbandonedRetentionMonths);
}
