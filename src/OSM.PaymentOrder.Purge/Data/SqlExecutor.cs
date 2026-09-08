using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OSM.PaymentOrder.Purge.Data;

public readonly record struct SqlParam(string Name, object? Value, SqlDbType? Type = null)
{
    public static SqlParam Of(string name, object? value) => new(name, value);

    /// <summary>
    /// Parametro con tipo dichiarato, dove l'inferenza non va bene. Con la
    /// mappa globale di DapperSetup un DateTime viaggia gia' come datetime2;
    /// il tipo esplicito resta per i parametri NULL, il cui tipo non si puo'
    /// dedurre dal valore, e per rendere l'intenzione leggibile nei punti
    /// in cui il tipo e' parte del contratto (la filigrana, gli anchor).
    /// </summary>
    public static SqlParam Typed(string name, object? value, SqlDbType type) =>
        new(name, value, type);

    /// <summary>
    /// I parametri nel formato di Dapper. L'elenco dei tipi e' chiuso: uno
    /// SqlDbType non previsto solleva qui, con il nome del parametro, invece
    /// di arrivare al server con un tipo indovinato.
    /// </summary>
    public static DynamicParameters ToDapper(IReadOnlyList<SqlParam> parameters)
    {
        var dp = new DynamicParameters();
        foreach (var p in parameters)
            dp.Add(p.Name, p.Value, p.Type is { } t ? ToDbType(p.Name, t) : null);
        return dp;
    }

    private static DbType ToDbType(string name, SqlDbType type) => type switch
    {
        SqlDbType.DateTime2 => DbType.DateTime2,
        SqlDbType.DateTime => DbType.DateTime,
        SqlDbType.DateTimeOffset => DbType.DateTimeOffset,
        SqlDbType.UniqueIdentifier => DbType.Guid,
        SqlDbType.Int => DbType.Int32,
        SqlDbType.BigInt => DbType.Int64,
        SqlDbType.Bit => DbType.Boolean,
        SqlDbType.NVarChar => DbType.String,
        SqlDbType.VarChar => DbType.AnsiString,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
                $"SqlDbType non mappato per il parametro {name}: aggiungerlo a SqlParam.ToDbType.")
    };
}

/// <summary>
/// Accesso ADO diretto. Il motore usa SQL raw e non referenzia alcun DbContext:
/// e' questo che consente di ospitarlo su un runtime diverso da quello
/// dell'applicazione (v10 §5).
/// </summary>
public sealed class SqlExecutor(string connectionString, int commandTimeoutSeconds = 300)
    : ISqlExecutor
{
    static SqlExecutor() => DapperSetup.Ensure();

    public string ConnectionString { get; } = connectionString;
    public int CommandTimeoutSeconds { get; } = commandTimeoutSeconds;

    private CommandDefinition Define(string sql, SqlParam[] parameters, CancellationToken ct,
                                     SqlTransaction? tx = null) =>
        new(sql, SqlParam.ToDapper(parameters), tx, CommandTimeoutSeconds, cancellationToken: ct);

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
        return await conn.ExecuteAsync(Define(sql, parameters, ct)).ConfigureAwait(false);
    }

    /// <summary>NULL dal server diventa default(T): zero per i conteggi, null per i nullable.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct,
                                         params SqlParam[] parameters)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<T>(Define(sql, parameters, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Mappa per nome di colonna su un record. E' la forma da preferire per
    /// il codice di produzione: una colonna aggiunta o spostata nella query
    /// non cambia in silenzio il significato di un GetInt32(5).
    /// </summary>
    public async Task<List<T>> QueryAsync<T>(string sql, CancellationToken ct,
                                             params SqlParam[] parameters)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<T>(Define(sql, parameters, ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IPurgeSession> BeginSessionAsync(CancellationToken ct) =>
        await SqlSession.OpenAsync(ConnectionString, CommandTimeoutSeconds, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Mappa posizionale da IDataRecord. Resta per i test e per le letture
    /// ad hoc; in produzione e' sostituita dalla variante per nome.
    /// </summary>
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
