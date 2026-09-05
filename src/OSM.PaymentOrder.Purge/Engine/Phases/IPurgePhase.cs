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
        phase is RunPhase.Completed or RunPhase.Failed or RunPhase.Aborted
            ? throw new ArgumentException(
                $"'{phase}' e' uno stato terminale: usare Complete() o Fail().", nameof(phase))
            : new(phase, false);

    public static PhaseResult Complete() => new(RunPhase.Completed, true);

    public static PhaseResult Fail(string error) => new(RunPhase.Failed, true, error);

    /// <summary>
    /// La fase non ha terminato il lavoro ma il run deve restare nella stessa
    /// fase (es. fine finestra operativa durante Executing).
    /// </summary>
    public static PhaseResult Stay() => new(null, true);
}
