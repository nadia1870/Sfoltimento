using Microsoft.Data.SqlClient;
using OSM.PaymentOrder.Purge.Data;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Lock di sessione su SQL Server: impedisce due istanze del purge
/// contemporanee.
///
/// Il lock vive quanto la connessione che lo tiene, e quella connessione resta
/// aperta per tutto il run — ore, potenzialmente. Se cade per un singhiozzo di
/// rete, SQL Server rilascia il lock alla chiusura della sessione **senza che
/// il processo se ne accorga**: da quel momento il run prosegue credendo di
/// essere solo, e una seconda istanza avviata dopo passerebbe. Per questo
/// l'handle sa dire se il lock e' ancora suo (EnsureHeldAsync), e chi lo
/// possiede lo verifica ai punti di ripresa.
/// </summary>
public sealed class PurgeExecutionLock(SqlExecutor sql)
{
    private const string Resource = "OSM.PaymentOrder.Purge.Retention";

    public async Task<IPurgeExecutionLease?> TryAcquireAsync(CancellationToken ct)
    {
        var conn = await sql.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = sql.Command(conn, null, """
                DECLARE @Result INT;
                EXEC @Result = sys.sp_getapplock
                    @Resource = @Resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = 0;
                SELECT @Result;
                """, SqlParam.Of("@Resource", Resource));

            var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (result < 0)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new Handle(conn, sql);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Handle(SqlConnection connection, SqlExecutor sql) : IPurgeExecutionLease
    {
        public async Task EnsureHeldAsync(CancellationToken ct)
        {
            if (connection.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException(Perso("la connessione non e' piu' aperta"));

            string mode;
            try
            {
                await using var cmd = sql.Command(connection, null, """
                    SELECT APPLOCK_MODE('public', @Resource, 'Session');
                    """, SqlParam.Of("@Resource", Resource));

                mode = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string ?? "NoLock";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(Perso("la verifica non e' riuscita"), ex);
            }

            if (!string.Equals(mode, "Exclusive", StringComparison.Ordinal))
                throw new InvalidOperationException(Perso($"APPLOCK_MODE riporta '{mode}'"));
        }

        private static string Perso(string dettaglio) =>
            $"Il lock di istanza non e' piu' garantito ({dettaglio}). Il run si ferma: " +
            "proseguire significherebbe rischiare due istanze del purge sullo stesso " +
            "database. Il run resta riprendibile dal checkpoint.";

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = sql.Command(connection, null, """
                    EXEC sys.sp_releaseapplock
                        @Resource = @Resource,
                        @LockOwner = 'Session';
                    """, SqlParam.Of("@Resource", Resource));
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch
            {
                // La chiusura della sessione rilascia comunque il lock.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
