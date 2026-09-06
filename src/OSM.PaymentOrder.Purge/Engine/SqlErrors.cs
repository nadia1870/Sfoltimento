using Microsoft.Data.SqlClient;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Quali errori di SQL Server valgono come guasto passeggero.
///
/// Stava dentro SliceExecutor come lista di tre numeri. Ora serve in due punti
/// — la slice, che riprova, e l'orchestratore, che decide se il run resta
/// riprendibile — e due liste diverse produrrebbero il caso peggiore: una slice
/// che riprova un errore che l'orchestratore considera definitivo.
///
/// L'elenco e' volutamente chiuso. Trattare come transitorio un errore che non
/// lo e' significa riprovare all'infinito una cancellazione che non passera'
/// mai; il costo opposto e' che un guasto non elencato chiude un run che si
/// sarebbe potuto riprendere, il che e' rumoroso ma innocuo.
/// </summary>
public static class SqlErrors
{
    /// <summary>Concorrenza con l'operativita': esiti attesi, non eccezionali.</summary>
    private static readonly HashSet<int> Concurrency = [1205, 1222, -2];

    /// <summary>
    /// Connessione caduta, persa o rifiutata. Sono i codici che si vedono
    /// quando il purge gira di notte non presidiato e la rete, il cluster o il
    /// servizio hanno un momento difficile.
    /// </summary>
    private static readonly HashSet<int> Connection =
    [
        0,      // errore di rete generico segnalato dal provider
        20,     // istanza non raggiungibile
        64,     // connessione terminata durante l'accesso
        121,    // semaforo scaduto
        233,    // connessione chiusa dal server
        596,    // impossibile continuare l'esecuzione
        615,    // database non trovato, tipico durante un failover
        921,    // database non ancora recuperato
        4060,   // apertura del database rifiutata
        4221,   // lettura su secondario prima della sincronizzazione
        10053,  // connessione interrotta dal software host
        10054,  // connessione azzerata dal peer
        10060,  // timeout di connessione
        10928, 10929,           // limiti di risorsa
        40197, 40501, 40613,    // errori di servizio e failover
        49918, 49919, 49920     // nessuna risorsa per elaborare la richiesta
    ];

    public static bool IsTransient(SqlException ex) =>
        Concurrency.Contains(ex.Number) || Connection.Contains(ex.Number);

    /// <summary>
    /// Un deadlock o un timeout di lock: la slice puo' riprovare subito.
    /// Distinta da IsTransient perche' un guasto di connessione non si risolve
    /// con un ritardo di cinque secondi, mentre una contesa si.
    /// </summary>
    public static bool IsConcurrency(SqlException ex) => Concurrency.Contains(ex.Number);
}
