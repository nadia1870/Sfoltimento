using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Secondo livello di protezione: la modalita' Delete richiede che la policy
/// corrente sia stata esaminata e approvata su questo database.
///
/// Il primo livello — la modalita' esplicita sulla riga di comando — impedisce
/// la cancellazione accidentale per configurazione sbagliata. Non impedisce
/// pero' che qualcuno pianifichi il job DELETE senza che nessuno abbia guardato
/// un report, ed e' quello che copre questo controllo.
///
/// L'approvazione vive nel database, quindi non viaggia con il codice: un
/// deploy in un ambiente nuovo trova una tabella vuota e non cancella niente
/// finche' qualcuno non esamina un dry-run prodotto li'.
///
/// Sta nei punti di ingresso e non nell'orchestratore di proposito. UC4 e la
/// riga di comando sono gli unici ingressi previsti, hanno un nome, e tenerlo
/// qui evita che i test di integrazione debbano aggirarlo — un aggiramento nei
/// test e' il primo passo verso un aggiramento in produzione.
/// </summary>
public sealed class PurgeApprovalGate(
    PurgeRunStore store,
    IOptions<PurgeOptions> options,
    ILogger<PurgeApprovalGate> log)
{
    private readonly PurgeOptions _options = options.Value;

    /// <summary>
    /// True se l'esecuzione puo' procedere. False e' un rifiuto gia'
    /// registrato a log: il chiamante deve uscire con codice diverso da zero,
    /// perche' un purge che non parte in silenzio sembrerebbe riuscito.
    /// </summary>
    public async Task<bool> IsAllowedAsync(PurgeExecutionMode mode, CancellationToken ct)
    {
        if (mode == PurgeExecutionMode.DryRun)
            return true;

        var hash = PurgePolicy.ComputeHash(_options);
        var approvazione = await store.FindApprovalAsync(hash, ct).ConfigureAwait(false);

        if (approvazione is null)
        {
            log.LogError(
                "PurgePolicyNotApproved Policy={Policy} Impronta={Hash}. " +
                "Nessuno ha approvato questa policy su questo database. " +
                "Eseguire un dry-run, esaminarne il report e approvarlo con " +
                "'purge approve <run-id> --by <nome>'.",
                PurgePolicy.Describe(_options), hash);

            return false;
        }

        log.LogInformation(
            "PurgePolicyApproved Policy={Policy} ApprovataDa={By} Il={On} DryRun={RunId}",
            PurgePolicy.Describe(_options), approvazione.ApprovedBy,
            approvazione.ApprovedOn, approvazione.DryRunRunId);

        return true;
    }
}
