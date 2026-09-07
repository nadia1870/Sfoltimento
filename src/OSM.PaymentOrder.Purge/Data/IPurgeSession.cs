using System.Data;

namespace OSM.PaymentOrder.Purge.Data;

/// <summary>
/// Il confine transazionale di una slice.
///
/// Non e' "ISqlExecutor con una transazione": e' una responsabilita' diversa.
/// ISqlExecutor rappresenta l'accesso SQL senza stato, uno statement per
/// connessione. IPurgeSession rappresenta l'unita' atomica su cui si regge
/// l'invariante del motore — un ordine non e' mai parzialmente cancellato — e
/// nel codice quel confine esisteva gia', dentro SliceExecutor, senza avere un
/// nome.
///
/// Dargli un nome serve a poterlo provare. SliceExecutor e' la classe piu'
/// critica del motore, e finche' lavorava su SqlConnection e SqlTransaction
/// concreti era l'unica non verificabile in isolamento: la sequenza degli
/// statement, la guardia sui collettivi, la rivalidazione del rowcount, i
/// quattro catch di D-7 si potevano osservare solo attraverso un run completo.
///
/// La superficie e' volutamente stretta: quattro metodi, esattamente cio' che
/// SliceExecutor usa. Due divieti espliciti, perche' sono le due direzioni in
/// cui questa interfaccia degenererebbe:
///
///   - Non esporre SqlConnection, SqlTransaction o SqlCommand. Un'interfaccia
///     che restituisce oggetti ADO sposta il problema senza risolverlo:
///     l'astrazione esprime operazioni, non oggetti.
///
///   - Non aggiungere ExecuteWithRetryAsync o simili. Il retry appartiene a
///     BatchExecutionCoordinator, e quel confine — SliceExecutor esegue una
///     volta sola, il coordinatore decide se riprovare — e' uno dei risultati
///     migliori del refactoring. Un metodo comodo qui lo cancellerebbe.
///
/// La sessione arriva con DEADLOCK_PRIORITY LOW gia' impostata e la transazione
/// aperta: sono politica del purge, non scelte del chiamante.
/// </summary>
public interface IPurgeSession : IAsyncDisposable
{
    Task<int> ExecuteAsync(string sql, CancellationToken ct, params SqlParam[] parameters);

    Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params SqlParam[] parameters);

    Task CommitAsync(CancellationToken ct);

    /// <summary>
    /// Annulla la transazione. Chiamarla piu' volte, o dopo il commit, non ha
    /// effetto: i percorsi di errore di SliceExecutor si sovrappongono e non
    /// devono doversi coordinare.
    /// </summary>
    Task RollbackAsync(CancellationToken ct);

    // Invariante della liberazione, che vale per ogni implementazione:
    //
    //     dopo DisposeAsync, la transazione e' committata oppure annullata,
    //     mai ancora attiva.
    //
    // Formulato cosi' e non come "Dispose annulla se non committata", perche'
    // e' la garanzia che conta ed e' l'unica verificabile. Se il rollback in
    // liberazione fallisce — connessione gia' caduta — l'invariante regge
    // ugualmente: la transazione muore con la connessione.
}

/// <summary>
/// Accesso al database. I primi tre metodi aprono e chiudono una connessione
/// per statement; BeginSessionAsync restituisce invece una connessione con
/// transazione, per il lavoro che deve essere atomico.
///
/// QueryAsync mappa da IDataRecord e non da SqlDataReader. E' la differenza fra
/// un'interfaccia sostituibile e una decorativa: SqlDataReader non e'
/// istanziabile in un test, IDataRecord si implementa in venti righe. Tutte le
/// lambda esistenti continuano a compilare, perche' nessuna dichiara il tipo.
/// </summary>
public interface ISqlExecutor
{
    Task<int> ExecuteAsync(string sql, CancellationToken ct, params SqlParam[] parameters);

    Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params SqlParam[] parameters);

    Task<List<TRow>> QueryAsync<TRow>(string sql, Func<IDataRecord, TRow> map,
                                      CancellationToken ct, params SqlParam[] parameters);

    Task<IPurgeSession> BeginSessionAsync(CancellationToken ct);
}
