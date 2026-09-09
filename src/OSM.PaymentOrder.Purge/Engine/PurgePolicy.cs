using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// L'impronta della configurazione che decide *cosa* viene cancellato.
///
/// Serve a legare un'approvazione a una policy invece che a un singolo run. Un
/// dry-run approvato certifica che qualcuno ha esaminato un report prodotto
/// con una certa policy; se quella policy cambia, l'approvazione non dice piu'
/// niente e va rifatta.
///
/// Dentro c'e' solo cio' che determina l'insieme dei candidati. Fuori restano
/// le manopole di prestazione — dimensione delle pagine, tetti per slice,
/// tentativi, finestra operativa, housekeeping — perche' cambiano quanto
/// lavoro si fa per volta, non cosa si cancella.
///
/// La distinzione non e' cosmetica. Includere SelectionBatchSize
/// significherebbe che tararlo dopo il collaudo, cosa che faremo di sicuro,
/// invalida l'approvazione e blocca il job notturno per una modifica innocua.
/// E' cosi' che un controllo di sicurezza perde credibilita' e finisce
/// disattivato.
/// </summary>
public static class PurgePolicy
{
    /// <summary>
    /// Impronta stabile fra esecuzioni e fra macchine: cultura invariante,
    /// strategie ordinate, campi separati da un carattere che non puo'
    /// comparire nei valori.
    /// </summary>
    public static string ComputeHash(PurgeOptions options)
    {
        var strategie = options.Strategies
            .Select(s => s.ToString())
            .OrderBy(s => s, StringComparer.Ordinal);

        var campi = new[]
        {
            "RetentionYears=" + options.RetentionYears.ToString(CultureInfo.InvariantCulture),
            "AnchorMode=" + options.AnchorMode,
            "AbandonedEnabled=" + options.AbandonedEnabled,
            "AbandonedRetentionMonths=" +
                options.AbandonedRetentionMonths.ToString(CultureInfo.InvariantCulture),
            "Strategies=" + string.Join(",", strategie),

            // MaxAggregateWeight sta qui e non fra i parametri operativi, che
            // sono fuori dall'impronta.
            //
            // La distinzione non e' fra parametri "importanti" e no: e' fra
            // cio' che decide COSA viene cancellato e cio' che decide QUANTO
            // lavoro si fa per volta. MaxRowsPerBatch cambia la dimensione
            // delle transazioni, ma l'insieme cancellato resta lo stesso;
            // MaxAggregateWeight decide se un aggregato viene cancellato o
            // resta a database, ed e' quindi perimetro.
            //
            // Il caso concreto: si approva un dry-run con il tetto a 30.000, e
            // il report mostra un collettivo da 80.000 fra gli esclusi. Chi
            // approva sta approvando anche quell'esclusione. Alzare poi il
            // tetto a 100.000 renderebbe cancellabile quell'aggregato con
            // l'approvazione precedente, che nessuno ha dato per lui.
            "MaxAggregateWeight=" +
                options.MaxAggregateWeight.ToString(CultureInfo.InvariantCulture),
        };

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", campi)));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// La stessa policy in forma leggibile. Chi approva deve vedere cosa sta
    /// approvando, non un'impronta esadecimale.
    /// </summary>
    public static string Describe(PurgeOptions options) =>
        $"Retention={options.RetentionYears} anni, Ancoraggio={options.AnchorMode}, " +
        $"Abbandonati={(options.AbandonedEnabled ? options.AbandonedRetentionMonths + " mesi" : "disattivi")}, " +
        $"Strategie=[{string.Join(", ", options.Strategies)}], " +
        $"TettoAggregato={(options.MaxAggregateWeight > 0 ? options.MaxAggregateWeight.ToString("N0") + " righe" : "nessuno")}";
}

/// <summary>
/// Un'approvazione registrata. La chiave e' l'impronta della policy: cio' che
/// viene approvato e' la policy, e il dry-run e' la prova che qualcuno l'ha
/// esaminata.
/// </summary>
public sealed record PolicyApproval(
    string PolicyHash,
    Guid DryRunRunId,
    DateTimeOffset ApprovedOn,
    string ApprovedBy,
    string? Note);
