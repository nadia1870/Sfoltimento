using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Estende la finestra operativa alle fasi che non la controllavano.
///
/// Il controllo su IsWithinWindow vive in BatchExecutionCoordinator e in
/// PurgeHousekeeping, cioe' nelle due parti che lavorano a lotti e possono
/// fermarsi fra un lotto e l'altro. Selecting, Expanding, Validating e
/// Planning non ce l'hanno: BatchedStatementRunner dichiara che «la
/// cancellazione arriva a fine finestra operativa», ma nessuno cancellava
/// niente — il token era quello dello spegnimento del servizio o del Ctrl-C.
/// Su volumi reali quelle fasi possono durare a lungo, e proseguivano dentro
/// l'orario lavorativo in concorrenza con l'operativita': esattamente cio' che
/// il pacing fra le slice, la priorita' bassa sui deadlock e le transazioni
/// corte esistono per evitare.
///
/// Il token prodotto qui non e' il meccanismo ordinario di chiusura. La notte
/// normale finisce con il coordinatore che smette di prendere slice e
/// restituisce WindowClosed, senza eccezioni. Questo token morde solo oltre la
/// tolleranza, quando una fase ha superato la finestra, ed e' per questo che
/// la sua scadenza si registra come errore e non come informazione.
///
/// L'orchestratore e' gia' pronto a riceverlo: una OperationCanceledException
/// non cambia la fase, non conta come interruzione e lascia il run
/// riprendibile dal checkpoint.
/// </summary>
public sealed class PurgeWindowGuard(
    IOptions<PurgeOptions> options,
    TimeProvider clock,
    ILogger<PurgeWindowGuard> log)
{
    /// <summary>
    /// Apre una finestra di esecuzione legata al token del chiamante.
    /// Il risultato va liberato con using.
    /// </summary>
    public PurgeWindowScope Open(CancellationToken outer)
    {
        var opzioni = options.Value;
        var residuo = opzioni.TimeUntilWindowEnd(opzioni.Now(clock));

        if (residuo is null)
        {
            // Finestra disattivata, o --no-window richiesto esplicitamente:
            // il chiamante ha gia' dichiarato di volerne fare a meno.
            log.LogDebug("Finestra operativa non attiva: nessuna scadenza imposta.");
            return PurgeWindowScope.Unbounded(outer);
        }

        var scadenza = residuo.Value + opzioni.WindowGrace;

        log.LogInformation(
            "PurgeWindowDeadline Chiusura={End} Residuo={Residuo} Tolleranza={Grace} " +
            "Scadenza={Scadenza}",
            opzioni.WindowEnd, residuo.Value, opzioni.WindowGrace, scadenza);

        var window = new CancellationTokenSource(scadenza, clock);

        window.Token.Register(() => log.LogError(
            "PurgeWindowOverrun: una fase ha superato la chiusura delle {End} di piu' " +
            "della tolleranza di {Grace}. L'esecuzione viene interrotta e il run resta " +
            "riprendibile dal checkpoint. Se accade con regolarita', la fase non sta " +
            "nella finestra e va misurata, non tollerata.",
            opzioni.WindowEnd, opzioni.WindowGrace));

        return PurgeWindowScope.BoundedBy(outer, window, scadenza);
    }
}

/// <summary>
/// Finestra di esecuzione: un token che unisce lo spegnimento del chiamante e
/// la scadenza della finestra operativa, e sa dire quale dei due ha vinto.
///
/// La distinzione serve al chiamante: uno spegnimento e' una richiesta, uno
/// sforamento e' un'anomalia, e i due non possono finire nello stesso ramo di
/// catch con lo stesso messaggio.
/// </summary>
public sealed class PurgeWindowScope : IDisposable
{
    private readonly CancellationTokenSource? _window;
    private readonly CancellationTokenSource? _linked;

    private PurgeWindowScope(
        CancellationToken token,
        CancellationTokenSource? window,
        CancellationTokenSource? linked,
        TimeSpan? deadline)
    {
        Token = token;
        Deadline = deadline;
        _window = window;
        _linked = linked;
    }

    /// <summary>Token da passare al lavoro. Mai CancellationToken.None.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Tempo concesso al momento dell'apertura, tolleranza inclusa. Null quando
    /// la finestra non e' attiva. Esposto perche' e' l'unica parte verificabile
    /// senza far scadere davvero un timer.
    /// </summary>
    public TimeSpan? Deadline { get; }

    /// <summary>
    /// True se a cancellare e' stata la finestra e non il chiamante. Da leggere
    /// dentro il catch, per non confondere uno sforamento con uno spegnimento.
    /// </summary>
    public bool ClosedByWindow => _window?.IsCancellationRequested ?? false;

    internal static PurgeWindowScope Unbounded(CancellationToken outer) =>
        new(outer, null, null, null);

    internal static PurgeWindowScope BoundedBy(
        CancellationToken outer, CancellationTokenSource window, TimeSpan deadline)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, window.Token);
        return new PurgeWindowScope(linked.Token, window, linked, deadline);
    }

    public void Dispose()
    {
        // Il linked prima del window: liberare la sorgente mentre qualcuno vi e'
        // ancora agganciato solleverebbe alla registrazione successiva.
        _linked?.Dispose();
        _window?.Dispose();
    }
}
