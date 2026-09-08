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

    /// <summary>
    /// Errore di integrita' dei dati: una riga specifica rifiuta la
    /// cancellazione o l'inserimento. Non e' un guasto e non passa da solo,
    /// ma e' circoscritto a un aggregato: dividere la slice puo' isolarlo
    /// (D-11). Lista chiusa come le altre, per la stessa ragione.
    /// </summary>
    private static readonly HashSet<int> DataIntegrity =
    [
        547,    // vincolo FK o CHECK violato
        2627,   // violazione di chiave primaria o univoca
        2601,   // indice univoco violato
    ];

    /// <summary>
    /// Guasto passeggero di qualunque natura: il run resta riprendibile.
    /// Serve all'orchestratore, che decide sul run intero.
    /// </summary>
    public static bool IsTransient(SqlException ex) => IsTransient(ex.Number);

    public static bool IsTransient(int number) =>
        Concurrency.Contains(number) || Connection.Contains(number);

    /// <summary>
    /// Contesa con l'operativita': la slice puo' riprovare fra qualche secondo.
    ///
    /// Distinta da IsTransient perche' le due decisioni sono diverse. Una
    /// contesa si risolve riprovando la stessa slice; una connessione caduta
    /// no, e riprovarla brucia i tentativi disponibili fino ad abbandonare la
    /// slice. Un'interruzione di rete diventerebbe cosi' un aggregato lasciato
    /// a database in via definitiva, invece di un run da riprendere.
    /// </summary>
    public static bool IsConcurrency(SqlException ex) => IsConcurrency(ex.Number);

    public static bool IsConcurrency(int number) => Concurrency.Contains(number);

    /// <summary>
    /// Errore di dati che giustifica la bisezione della slice. Volutamente
    /// disgiunto da IsTransient: un 547 non si risolve riprovando, e un
    /// deadlock non si risolve dividendo.
    /// </summary>
    public static bool IsDataIntegrity(SqlException ex) => IsDataIntegrity(ex.Number);

    public static bool IsDataIntegrity(int number) => DataIntegrity.Contains(number);
}
