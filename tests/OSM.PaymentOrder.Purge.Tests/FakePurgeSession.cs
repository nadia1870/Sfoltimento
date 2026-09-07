using OSM.PaymentOrder.Purge.Data;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Sessione transazionale finta, per provare SliceExecutor senza database.
///
/// Registra gli statement nell'ordine in cui arrivano e conta commit e
/// rollback. Serve a verificare cose che prima si potevano solo dedurre da un
/// run completo: che dopo un guasto al terzo statement il quarto non parte, che
/// il checkpoint non viene scritto se la rivalidazione fallisce, e che in ogni
/// percorso diverso dal successo CommitAsync non viene chiamato mai.
/// </summary>
internal sealed class FakePurgeSession : IPurgeSession
{
    /// <summary>Statement ricevuti, nell'ordine. Include audit e checkpoint.</summary>
    public List<string> Executed { get; } = [];

    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Righe restituite da ExecuteAsync quando nessuna regola combacia.</summary>
    public int DefaultRowCount { get; init; } = 1;

    /// <summary>
    /// Righe per statement che contiene la chiave. Serve a simulare la DELETE
    /// dell'ordine che ne cancella meno del previsto.
    /// </summary>
    public Dictionary<string, int> RowCountByFragment { get; init; } = [];

    /// <summary>Valore della guardia sui collettivi.</summary>
    public int ScalarResult { get; init; }

    /// <summary>Eccezione sollevata all'ennesima ExecuteAsync, contando da uno.</summary>
    public int FailAtStatement { get; init; }
    public Exception? Failure { get; init; }

    /// <summary>Eccezione sollevata dal commit, dopo che tutto il resto e' passato.</summary>
    public Exception? CommitFailure { get; init; }

    /// <summary>Eccezione sollevata dal rollback, per il caso in cui fallisca anche quello.</summary>
    public Exception? RollbackFailure { get; init; }

    private bool _closed;

    public Task<int> ExecuteAsync(string sql, CancellationToken ct, params SqlParam[] parameters)
    {
        ct.ThrowIfCancellationRequested();
        Executed.Add(sql);

        if (Failure is not null && Executed.Count == FailAtStatement)
            throw Failure;

        foreach (var (frammento, righe) in RowCountByFragment)
            if (sql.Contains(frammento, StringComparison.Ordinal))
                return Task.FromResult(righe);

        return Task.FromResult(DefaultRowCount);
    }

    public Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params SqlParam[] parameters)
    {
        ct.ThrowIfCancellationRequested();
        Executed.Add(sql);
        return Task.FromResult((T?)Convert.ChangeType(ScalarResult, typeof(T)));
    }

    public Task CommitAsync(CancellationToken ct)
    {
        if (CommitFailure is not null) throw CommitFailure;
        Commits++;
        _closed = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        if (_closed) return Task.CompletedTask;
        _closed = true;
        Rollbacks++;
        if (RollbackFailure is not null) throw RollbackFailure;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Quanti statement contengono il frammento indicato.</summary>
    public int CountContaining(string frammento) =>
        Executed.Count(s => s.Contains(frammento, StringComparison.Ordinal));
}

/// <summary>
/// ISqlExecutor che restituisce sempre la sessione data. I tre metodi autonomi
/// non servono a SliceExecutor e sollevano: se un giorno li usasse, il test
/// deve accorgersene invece di ricevere silenziosamente un valore finto.
/// </summary>
internal sealed class FakeSqlExecutor(IPurgeSession session, Exception? openFailure = null)
    : ISqlExecutor
{
    public Task<int> ExecuteAsync(string sql, CancellationToken ct, params SqlParam[] p) =>
        throw new InvalidOperationException("SliceExecutor non deve usare l'accesso senza stato.");

    public Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params SqlParam[] p) =>
        throw new InvalidOperationException("SliceExecutor non deve usare l'accesso senza stato.");

    public Task<List<TRow>> QueryAsync<TRow>(string sql, Func<System.Data.IDataRecord, TRow> map,
        CancellationToken ct, params SqlParam[] p) =>
        throw new InvalidOperationException("SliceExecutor non deve usare l'accesso senza stato.");

    public Task<IPurgeSession> BeginSessionAsync(CancellationToken ct) =>
        openFailure is not null
            ? throw openFailure
            : Task.FromResult(session);
}
