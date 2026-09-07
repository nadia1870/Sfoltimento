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
    ISqlExecutor sql,
    PurgeStrategyResolver strategyResolver,
    PurgeMetrics metrics,
    ILogger<SliceExecutor> log)
{
    public async Task<SliceResult> ExecuteAsync(
        PurgeRun run, SliceInfo slice, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var strategy = strategyResolver.Resolve(run.Strategy);

        // L'apertura della sessione sta dentro il try.
        //
        // Stava fuori, e una connessione che non si apre e' esattamente cio'
        // che accade quando la rete ha un singhiozzo alle due di notte:
        // l'eccezione scavalcava i catch e risaliva senza essere classificata.
        // Qui passa dai quattro catch come qualunque altro guasto.
        //
        // La sessione arriva con DEADLOCK_PRIORITY LOW e la transazione gia'
        // aperta: sono politica del purge e vivono nell'implementazione.
        IPurgeSession? session = null;

        try
        {
            session = await sql.BeginSessionAsync(ct).ConfigureAwait(false);

            var totalRows = 0;
            var orderRows = 0;

            // Traccia per tabella. Raccolta in memoria e scritta in un solo
            // statement prima del commit: e' l'unico modo di registrarla senza
            // allungare la transazione di un round-trip per tabella.
            var auditLines = new List<(string Table, int Rows)>();

            if (strategy.Type == RetentionStrategy.Collective)
            {
                // ScalarAsync<int> invece di Convert.ToInt32 su un object:
                // il contratto su NULL diventa dichiarato (zero) invece di
                // dipendere dal comportamento di Convert.
                var invalidCollectives = await session.ScalarAsync<int>(
                    RetentionSql.ValidateCollectiveBatchIntegrity, ct,
                    SqlParam.Of("@RunId", run.RunId),
                    SqlParam.Of("@BatchNo", slice.BatchNo)).ConfigureAwait(false);

                if (invalidCollectives > 0)
                {
                    await session.RollbackAsync(ct).ConfigureAwait(false);
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

                var affected = await session.ExecuteAsync(statement, ct,
                    SqlParam.Of("@RunId", run.RunId),
                    SqlParam.Of("@BatchNo", slice.BatchNo)).ConfigureAwait(false);
                totalRows += affected;
                if (table == "Order") orderRows = affected;

                // Le tabelle a zero righe non entrano nell'audit: sono la
                // maggioranza in ogni slice (un ordine ha un solo tipo di
                // dettaglio) e scriverle renderebbe la traccia illeggibile.
                if (affected > 0) auditLines.Add((table, affected));

                metrics.RowsDeleted(table, run.Strategy.ToString(), affected);
            }

            // Rivalidazione: se un ordine ha cambiato stato fra selezione ed
            // esecuzione, la DELETE del gruppo 4 ne cancella meno del previsto.
            // Procedere lascerebbe a database un ordine privo di storico e
            // dettagli, che e' molto peggio del non fare nulla.
            if (strategy.PlanningMode != PurgePlanningMode.OrphanHistory && orderRows != slice.OrderCount)
            {
                await session.RollbackAsync(ct).ConfigureAwait(false);
                log.LogWarning(
                    "PurgeSliceRetried RunId={RunId} BatchNo={BatchNo} Atteso={Expected} " +
                    "Cancellato={Actual} Motivo=StatoOrdineCambiato",
                    run.RunId, slice.BatchNo, slice.OrderCount, orderRows);
                return SliceResult.Retryable("StatusChangedDuringExecution");
            }

            if (auditLines.Count > 0)
                await WriteAuditAsync(session, run.RunId, slice.BatchNo, auditLines, ct)
                    .ConfigureAwait(false);

            await session.ExecuteAsync(RetentionSql.CheckpointSlice, ct,
                SqlParam.Of("@RunId", run.RunId),
                SqlParam.Of("@BatchNo", slice.BatchNo),
                SqlParam.Of("@RowsDeleted", totalRows)).ConfigureAwait(false);

            await session.CommitAsync(ct).ConfigureAwait(false);

            var elapsed = DateTimeOffset.UtcNow - started;
            metrics.SliceCompleted(elapsed, totalRows, run.Strategy.ToString());

            log.LogDebug(
                "PurgeSliceCompleted RunId={RunId} BatchNo={BatchNo} Ordini={Orders} " +
                "Righe={Rows} Oversized={Oversized} DurataMs={Ms}",
                run.RunId, slice.BatchNo, orderRows, totalRows, slice.IsOversized,
                elapsed.TotalMilliseconds);

            return SliceResult.Ok(totalRows);
        }
        // IsConcurrency, non IsTransient: qui si decide se riprovare *questa
        // slice*. Un deadlock si risolve riprovando la stessa slice fra qualche
        // secondo; una connessione caduta no. I due casi sono separati, e il
        // secondo e' gestito dal catch piu' sotto.
        catch (SqlException ex) when (SqlErrors.IsConcurrency(ex))
        {
            await SafeRollbackAsync(session, ct).ConfigureAwait(false);
            log.LogWarning(ex, "PurgeSliceRetried RunId={RunId} BatchNo={BatchNo} Sql={Number}",
                run.RunId, slice.BatchNo, ex.Number);
            return SliceResult.Retryable($"Sql{ex.Number}");
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (SqlException ex) when (SqlErrors.IsTransient(ex))
        {
            // Transitorio ma non di concorrenza: connessione caduta, rifiutata
            // o azzerata. Non e' un problema di questa slice e non si risolve
            // riprovandola.
            //
            // Questo catch deve esistere, e deve stare dopo quello sulla
            // concorrenza. Senza, l'eccezione finirebbe nel catch generico e
            // diventerebbe Fatal, che il coordinatore traduce in AbandonAsync:
            // un guasto di rete lascerebbe aggregati a database in via
            // definitiva, senza nemmeno consumare i tentativi previsti.
            //
            // Rilanciando, risale al coordinatore, che non cattura, e da li'
            // all'orchestratore, che la riconosce come guasto e lascia il run
            // riprendibile dal checkpoint.
            await SafeRollbackAsync(session, CancellationToken.None).ConfigureAwait(false);

            log.LogWarning(ex,
                "PurgeSliceInterrupted RunId={RunId} BatchNo={BatchNo} Sql={Number} — " +
                "guasto di connessione, il run resta riprendibile",
                run.RunId, slice.BatchNo, ex.Number);

            throw;
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(session, ct).ConfigureAwait(false);
            log.LogError(ex, "PurgeSliceAbandoned RunId={RunId} BatchNo={BatchNo}",
                run.RunId, slice.BatchNo);
            return SliceResult.Fatal(ex.Message);
        }
        finally
        {
            // La sessione nasce dentro il try, quindi niente await using: la
            // liberazione va fatta qui. Vale l'invariante di IPurgeSession —
            // dopo Dispose la transazione e' committata o annullata, mai ancora
            // attiva — che copre i percorsi che i catch non prevedono.
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resta un metodo privato di SliceExecutor e non diventa un metodo di
    /// IPurgeSession: l'audit e' logica di dominio del purge, e l'interfaccia
    /// deve restare la superficie stretta che e'. Il chiamante non tocca piu'
    /// connessione ne' transazione grazie all'astrazione, non grazie a questo.
    /// </summary>
    private static async Task WriteAuditAsync(
        IPurgeSession session,
        Guid runId,
        int batchNo,
        IReadOnlyList<(string Table, int Rows)> lines,
        CancellationToken ct)
    {
        var parameters = new SqlParam[2 + lines.Count * 2];
        parameters[0] = SqlParam.Of("@RunId", runId);
        parameters[1] = SqlParam.Of("@BatchNo", batchNo);

        for (var i = 0; i < lines.Count; i++)
        {
            parameters[2 + i * 2] = SqlParam.Of($"@T{i}", lines[i].Table);
            parameters[3 + i * 2] = SqlParam.Of($"@R{i}", (long)lines[i].Rows);
        }

        await session.ExecuteAsync(
            RetentionSql.InsertSliceAudit(lines.Count), ct, parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Il rollback nei catch e' esplicito e non delegato alla liberazione,
    /// perche' qui non e' pulizia: e' parte osservabile del percorso d'errore.
    /// Se fallisce, quella riga di log e' l'unico posto in cui la cosa compare.
    /// La rete di sicurezza in DisposeAsync copre un percorso diverso, non
    /// previsto, e il flag interno impedisce il doppio rollback.
    /// </summary>
    private async Task SafeRollbackAsync(IPurgeSession? session, CancellationToken ct)
    {
        if (session is null) return;   // il guasto e' avvenuto prima di aprirla

        try { await session.RollbackAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { log.LogError(ex, "Rollback fallito"); }
    }
}
