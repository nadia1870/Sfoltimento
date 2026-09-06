using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Data;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Esegue a pagine uno statement che avanza su una chiave ordinata.
///
/// Lo statement riceve @LastAnchor e @LastId, restituisce quante righe ha
/// esaminato, quante ne ha inserite e dove si e' fermato. Il ciclo termina
/// quando una pagina risulta incompleta, cioe' quando la sorgente e' esaurita.
///
/// Ogni pagina e' una transazione a se': la selezione non e' piu' atomica, ed
/// e' esattamente cio' che si vuole. Il set di candidati diventa completo solo
/// a fine ciclo, ma l'unico consumatore e' la fase successiva dello stesso
/// run, che non parte prima. Un'interruzione lascia una selezione parziale che
/// la riesecuzione completa, perche' gli statement sono idempotenti.
/// </summary>
public sealed class BatchedStatementRunner(
    SqlExecutor sql,
    IOptions<PurgeOptions> options,
    ILogger<BatchedStatementRunner> log)
{
    private readonly PurgeOptions _options = options.Value;

    private sealed record Page(int Inserted, int Scanned, DateTime? NextAnchor, Guid? NextId);

    public async Task<int> RunAsync(
        string label,
        Guid runId,
        string statement,
        CancellationToken ct,
        params SqlParam[] extra)
    {
        var batchSize = _options.SelectionBatchSize;
        var anchor = DateTime.MinValue;
        var lastId = Guid.Empty;
        var inserted = 0;
        var scanned = 0;
        var pages = 0;

        while (true)
        {
            // La cancellazione arriva a fine finestra operativa. Interrompere
            // fra una pagina e l'altra e' sicuro: nessuna transazione aperta,
            // e la fase non e' stata avanzata.
            ct.ThrowIfCancellationRequested();

            var parameters = new List<SqlParam>(extra.Length + 4)
            {
                SqlParam.Of("@RunId", runId),
                SqlParam.Of("@BatchSize", batchSize),
                SqlParam.Typed("@LastAnchor", anchor, SqlDbType.DateTime2),
                SqlParam.Typed("@LastId", lastId, SqlDbType.UniqueIdentifier),
            };
            parameters.AddRange(extra);

            var rows = await sql.QueryAsync(
                statement,
                r => new Page(
                    r.GetInt32(0),
                    r.GetInt32(1),
                    r.IsDBNull(2) ? null : r.GetDateTime(2),
                    r.IsDBNull(3) ? null : r.GetGuid(3)),
                ct,
                [.. parameters]).ConfigureAwait(false);

            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{label}: lo statement non ha restituito la riga di avanzamento.");
            }

            var page = rows[0];
            inserted += page.Inserted;
            scanned += page.Scanned;
            pages++;

            if (page.Scanned == 0)
                break;

            var nextAnchor = page.NextAnchor ?? anchor;
            var nextId = page.NextId ?? lastId;

            // Senza questo controllo una filigrana che non avanza produce un
            // ciclo infinito che cancella e reinserisce le stesse righe per
            // tutta la notte. Non dovrebbe accadere, perche' la chiave e'
            // univoca, ma il modo in cui fallirebbe e' troppo brutto per
            // lasciarlo alla teoria.
            if (nextAnchor == anchor && nextId == lastId)
            {
                throw new InvalidOperationException(
                    $"{label}: la chiave di avanzamento non progredisce " +
                    $"(Anchor={anchor:O}, Id={lastId}). Statement interrotto.");
            }

            anchor = nextAnchor;
            lastId = nextId;

            if (page.Scanned < batchSize)
                break;
        }

        log.LogInformation(
            "{Label} RunId={RunId} Inserite={Inserted} Esaminate={Scanned} " +
            "Pagine={Pages} Pagina={BatchSize}",
            label, runId, inserted, scanned, pages, batchSize);

        return inserted;
    }
}
