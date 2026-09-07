using System.Data;
using Microsoft.Data.SqlClient;

namespace OSM.PaymentOrder.Purge.Data;

/// <summary>
/// Implementazione reale di <see cref="IPurgeSession"/> su ADO.
///
/// La transazione viene annullata alla liberazione se non e' stata committata.
/// E' una rete di sicurezza, non il percorso previsto: SliceExecutor annulla
/// esplicitamente nei propri catch, perche' vuole registrare a log un rollback
/// fallito. Il flag evita che le due strade si pestino i piedi.
/// </summary>
internal sealed class SqlSession : IPurgeSession
{
    private readonly SqlConnection _conn;
    private readonly SqlTransaction _tx;
    private readonly int _timeoutSeconds;
    private bool _closed;

    private SqlSession(SqlConnection conn, SqlTransaction tx, int timeoutSeconds)
    {
        _conn = conn;
        _tx = tx;
        _timeoutSeconds = timeoutSeconds;
    }

    internal static async Task<IPurgeSession> OpenAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        SqlTransaction tx;

        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // In caso di deadlock con l'applicazione la vittima designata e' il
            // purge, mai l'operativita'. Va impostata sulla sessione prima di
            // aprire la transazione.
            await using (var pri = conn.CreateCommand())
            {
                pri.CommandText = "SET DEADLOCK_PRIORITY LOW;";
                pri.CommandTimeout = timeoutSeconds;
                await pri.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx = (SqlTransaction)await conn
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        }
        catch
        {
            // Un guasto qui lascerebbe altrimenti la connessione nel pool in
            // uno stato indefinito. L'eccezione prosegue: e' il chiamante a
            // classificarla.
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SqlSession(conn, tx, timeoutSeconds);
    }

    private SqlCommand Command(string sql, SqlParam[] parameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.Transaction = _tx;

        foreach (var p in parameters)
        {
            if (p.Type is { } type)
                cmd.Parameters.Add(p.Name, type).Value = p.Value ?? DBNull.Value;
            else
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        }

        return cmd;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct,
                                        params SqlParam[] parameters)
    {
        await using var cmd = Command(sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct,
                                         params SqlParam[] parameters)
    {
        await using var cmd = Command(sql, parameters);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (value is null || value == DBNull.Value) return default;
        if (value is T typed) return typed;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await _tx.CommitAsync(ct).ConfigureAwait(false);
        _closed = true;
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        if (_closed) return;

        // Il flag si alza prima: se il rollback fallisce, ritentarlo alla
        // liberazione solleverebbe una seconda eccezione che coprirebbe la
        // prima, che e' quella che interessa.
        _closed = true;
        await _tx.RollbackAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            try { await _tx.RollbackAsync().ConfigureAwait(false); }
            catch { /* la connessione potrebbe essere gia' caduta */ }
        }

        await _tx.DisposeAsync().ConfigureAwait(false);
        await _conn.DisposeAsync().ConfigureAwait(false);
    }
}
