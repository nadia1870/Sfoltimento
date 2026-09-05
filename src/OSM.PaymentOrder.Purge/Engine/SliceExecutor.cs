using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Observability;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Esegue una slice di aggregati in UNA transazione (§6.2, §6.4).
///
/// Invariante: un ordine non e' mai parzialmente cancellato. O la transazione
/// committa integralmente, o il rollback la riporta allo stato iniziale.
/// </summary>
public sealed class SliceExecutor(
    SqlExecutor sql,
    PurgeStrategyResolver strategyResolver,
    PurgeMetrics metrics,
    ILogger<SliceExecutor> log)
{
    public async Task<SliceResult> ExecuteAsync(
        PurgeRun run, SliceInfo slice, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var strategy = strategyResolver.Resolve(run.Strategy);

        await using var conn = await sql.OpenAsync(ct).ConfigureAwait(false);

        // In caso di deadlock con l'applicazione la vittima designata e' il
        // purge, mai l'operativita'.
        await using (var pri = sql.Command(conn, null, "SET DEADLOCK_PRIORITY LOW;"))
            await pri.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var tx = (SqlTransaction)await conn
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        try
        {
            var totalRows = 0;
            var orderRows = 0;

            if (strategy.Type == RetentionStrategy.Collective)
            {
                await using var guard = sql.Command(conn, tx, RetentionSql.ValidateCollectiveBatchIntegrity,
                    SqlParam.Of("@RunId", run.RunId),
                    SqlParam.Of("@BatchNo", slice.BatchNo));
                var invalidCollectives = Convert.ToInt32(
                    await guard.ExecuteScalarAsync(ct).ConfigureAwait(false));

                if (invalidCollectives > 0)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    log.LogWarning(
                        "PurgeSliceRetried RunId={RunId} BatchNo={BatchNo} " +
                        "Motivo=CollectiveNonAtomico CollettiviInvalidi={Count}",
                        run.RunId, slice.BatchNo, invalidCollectives);
                    return SliceResult.Retryable("CollectiveAtomicityViolation");
                }
            }

            var statements = strategy.GetSliceStatements();

            foreach (var (table, statement) in statements)
            {
                ct.ThrowIfCancellationRequested();

                await using var cmd = sql.Command(conn, tx, statement,
                    SqlParam.Of("@RunId", run.RunId),
                    SqlParam.Of("@BatchNo", slice.BatchNo));

                var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                totalRows += affected;
                if (table == "Order") orderRows = affected;

                metrics.RowsDeleted(table, run.Strategy.ToString(), affected);
            }

            // Rivalidazione: se un ordine ha cambiato stato fra selezione ed
            // esecuzione, la DELETE del gruppo 4 ne cancella meno del previsto.
            // Procedere lascerebbe a database un ordine privo di storico e
            // dettagli, che e' molto peggio del non fare nulla.
            if (strategy.PlanningMode != PurgePlanningMode.OrphanHistory && orderRows != slice.OrderCount)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                log.LogWarning(
                    "PurgeSliceRetried RunId={RunId} BatchNo={BatchNo} Atteso={Expected} " +
                    "Cancellato={Actual} Motivo=StatoOrdineCambiato",
                    run.RunId, slice.BatchNo, slice.OrderCount, orderRows);
                return SliceResult.Retryable("StatusChangedDuringExecution");
            }

            await using (var chk = sql.Command(conn, tx, RetentionSql.CheckpointSlice,
                            SqlParam.Of("@RunId", run.RunId),
                            SqlParam.Of("@BatchNo", slice.BatchNo),
                            SqlParam.Of("@RowsDeleted", totalRows)))
                await chk.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var elapsed = DateTimeOffset.UtcNow - started;
            metrics.SliceCompleted(elapsed, totalRows, run.Strategy.ToString());

            log.LogDebug(
                "PurgeSliceCompleted RunId={RunId} BatchNo={BatchNo} Ordini={Orders} " +
                "Righe={Rows} Oversized={Oversized} DurataMs={Ms}",
                run.RunId, slice.BatchNo, orderRows, totalRows, slice.IsOversized,
                elapsed.TotalMilliseconds);

            return SliceResult.Ok(totalRows);
        }
        catch (SqlException ex) when (IsTransient(ex))
        {
            await SafeRollbackAsync(tx, ct).ConfigureAwait(false);
            log.LogWarning(ex, "PurgeSliceRetried RunId={RunId} BatchNo={BatchNo} Sql={Number}",
                run.RunId, slice.BatchNo, ex.Number);
            return SliceResult.Retryable($"Sql{ex.Number}");
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(tx, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(tx, ct).ConfigureAwait(false);
            log.LogError(ex, "PurgeSliceAbandoned RunId={RunId} BatchNo={BatchNo}",
                run.RunId, slice.BatchNo);
            return SliceResult.Fatal(ex.Message);
        }
    }

    // 1205 deadlock, -2 timeout, 1222 lock request timeout: su un purge in
    // concorrenza con l'OLTP sono esiti attesi, non eccezionali.
    private static bool IsTransient(SqlException ex) =>
        ex.Number is 1205 or -2 or 1222;

    private async Task SafeRollbackAsync(SqlTransaction tx, CancellationToken ct)
    {
        try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { log.LogError(ex, "Rollback fallito"); }
    }
}
