using System.Data;
using Microsoft.Data.SqlClient;

namespace OSM.PaymentOrder.Purge.Data;

public readonly record struct SqlParam(string Name, object? Value, SqlDbType? Type = null)
{
    public static SqlParam Of(string name, object? value) => new(name, value);

    /// <summary>
    /// Parametro con tipo dichiarato, dove l'inferenza di AddWithValue non va
    /// bene. Un DateTime diventa SqlDbType.DateTime, il cui minimo e' il 1753:
    /// la data sentinella da cui parte la paginazione solleverebbe
    /// un'eccezione prima ancora di raggiungere il server.
    /// </summary>
    public static SqlParam Typed(string name, object? value, SqlDbType type) =>
        new(name, value, type);
}

/// <summary>
/// Accesso ADO diretto. Il motore usa SQL raw e non referenzia alcun DbContext:
/// e' questo che consente di ospitarlo su un runtime diverso da quello
/// dell'applicazione (v10 §5).
/// </summary>
public sealed class SqlExecutor(string connectionString, int commandTimeoutSeconds = 300)
    : ISqlExecutor
{
    public string ConnectionString { get; } = connectionString;
    public int CommandTimeoutSeconds { get; } = commandTimeoutSeconds;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public SqlCommand Command(SqlConnection conn, SqlTransaction? tx, string sql,
                              params SqlParam[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        if (tx is not null) cmd.Transaction = tx;
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
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, null, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct,
                                         params SqlParam[] parameters)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, null, sql, parameters);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (value is null || value == DBNull.Value) return default;
        // Guid non implementa IConvertible: il cast diretto va tentato per primo.
        if (value is T typed) return typed;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task<IPurgeSession> BeginSessionAsync(CancellationToken ct) =>
        await SqlSession.OpenAsync(ConnectionString, CommandTimeoutSeconds, ct)
            .ConfigureAwait(false);

    public async Task<List<TRow>> QueryAsync<TRow>(string sql,
        Func<IDataRecord, TRow> map, CancellationToken ct, params SqlParam[] parameters)
    {
        var rows = new List<TRow>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, null, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(map(reader));
        return rows;
    }
}
