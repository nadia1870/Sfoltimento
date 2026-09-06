using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.Phases;

/// <summary>
/// Una fase del workflow di retention. La fase esegue il proprio lavoro e
/// restituisce solamente l'esito della transizione; il persistere la nuova
/// fase resta responsabilita' dell'orchestratore.
/// </summary>
public interface IPurgePhase
{
    /// <summary>Fase primaria gestita dall'handler.</summary>
    RunPhase Phase { get; }

    /// <summary>
    /// Fasi persistite che possono essere riprese da questo handler.
    /// Selecting gestisce anche Created per mantenere compatibilita' con i run
    /// creati dalla versione precedente.
    /// </summary>
    IReadOnlySet<RunPhase> HandledPhases { get; }

    Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct);
}

public sealed record PhaseResult(
    RunPhase? NextPhase,
    bool Stop,
    string? Error = null)
{
    /// <summary>
    /// Transizione verso una fase che ha un handler. Gli stati terminali non
    /// ne hanno: passarli qui produrrebbe un ciclo che cerca un handler
    /// inesistente, quindi vengono rifiutati subito.
    /// </summary>
    public static PhaseResult Next(RunPhase phase) =>
        RunPhases.IsTerminal(phase)
            ? throw new ArgumentException(
                $"'{phase}' e' uno stato terminale: usare Complete() o Fail().", nameof(phase))
            : new(phase, false);

    public static PhaseResult Complete() => new(RunPhase.Completed, true);

    /// <summary>
    /// Il lavoro e' finito ma qualcosa e' rimasto indietro.
    ///
    /// Un run che abbandona delle slice chiudeva in Completed, e la differenza
    /// viveva in una riga di log. Sono due fatti operativi distinti: nel primo
    /// caso non c'e' niente da fare, nel secondo ci sono aggregati che nessuno
    /// ha cancellato e che nessun run successivo ripeschera' senza che qualcuno
    /// li guardi. L'housekeeping trattava gia' i due casi in modo diverso, ma
    /// deducendolo da RunBatchProgress invece che dallo stato del run.
    /// </summary>
    public static PhaseResult CompleteWithErrors(string error) =>
        new(RunPhase.CompletedWithErrors, true, error);

    public static PhaseResult Fail(string error) => new(RunPhase.Failed, true, error);

    /// <summary>
    /// La fase non ha terminato il lavoro ma il run deve restare nella stessa
    /// fase (es. fine finestra operativa durante Executing).
    /// </summary>
    public static PhaseResult Stay() => new(null, true);
}
