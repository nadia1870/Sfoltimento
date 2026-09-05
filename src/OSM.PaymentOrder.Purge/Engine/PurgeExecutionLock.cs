using Microsoft.Data.SqlClient;
using OSM.PaymentOrder.Purge.Data;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>SQL Server session lock: impedisce due istanze del purge contemporanee.</summary>
public sealed class PurgeExecutionLock(SqlExecutor sql)
{
    private const string Resource = "OSM.PaymentOrder.Purge.Retention";

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
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

    private sealed class Handle(SqlConnection connection, SqlExecutor sql) : IAsyncDisposable
    {
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
