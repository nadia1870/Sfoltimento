using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Componente mantenuto solo per compatibilita' con run V5 precedenti.
///
/// I nuovi run NON devono mai arrivare qui: un Collective viene cancellato
/// nella stessa transazione dei suoi Order componenti dal SliceExecutor.
/// Eseguire una delete differita qui violerebbe l'invariante di atomicita'.
/// </summary>
public sealed class CollectiveTailExecutor(
    SqlExecutor sql,
    ILogger<CollectiveTailExecutor> log)
{
    public async Task<bool> ExecuteAsync(PurgeRun run, CancellationToken ct)
    {
        if (run.Strategy != RetentionStrategy.Collective)
            return true;

        var selected = await sql.ScalarAsync<long>("""
            SELECT COUNT_BIG(*)
            FROM Purge.RunCandidateCollective
            WHERE RunId = @RunId AND State = 'Selected';
            """, ct, SqlParam.Of("@RunId", run.RunId)).ConfigureAwait(false);

        if (selected > 0)
        {
            log.LogError(
                "CollectiveTail legacy non eseguibile in V5.1: RunId={RunId} " +
                "ha {Count} Collective ancora Selected. L'esecuzione atomica deve avvenire " +
                "all'interno della slice.", run.RunId, selected);
            throw new InvalidOperationException(
                $"Run {run.RunId}: CollectiveTail legacy non supportato; " +
                "il Collective deve essere cancellato nella stessa transazione degli ordini.");
        }

        return true;
    }
}
