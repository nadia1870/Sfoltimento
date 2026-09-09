namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Controlli di configurazione che devono precedere qualunque cancellazione
/// reale, e che nessun percorso di esecuzione può aggirare (D-20).
///
/// È una funzione, non un pezzo di Program: la sua correttezza dipende da
/// combinazioni di modalità, opzioni e argomenti, e verificarle avviando
/// l'host costerebbe molto più che verificarle qui. La versione precedente
/// viveva dentro Program ed era coperta solo da un controllo sul testo del
/// sorgente, che non poteva accorgersi di un difetto di logica — e infatti
/// non se ne è accorto.
/// </summary>
public static class PurgeStartupGuard
{
    public const string NoWindowFlag = "--no-window";

    /// <summary>
    /// Il motivo per cui l'esecuzione va rifiutata, oppure null se può
    /// procedere.
    ///
    /// Restituisce un messaggio invece di un booleano perché chi legge il
    /// rifiuto alle due di notte deve sapere quale valore manca e quale
    /// sarebbe stato usato al suo posto.
    /// </summary>
    public static string? RejectionReason(
        PurgeExecutionMode mode, PurgeOptions options, IReadOnlyList<string> args)
    {
        // Solo la cancellazione reale. La simulazione resta permissiva: serve
        // a produrre il report da approvare, non tocca nulla, e pretendere il
        // fuso li' bloccherebbe il percorso che porta all'approvazione.
        if (mode != PurgeExecutionMode.Delete) return null;

        // Senza finestra il fuso non governa niente, quindi non serve
        // pretenderlo. --no-window e' gia' una dichiarazione esplicita, viene
        // registrata nel log, e rifiutarla qui renderebbe impossibile la sola
        // via d'uscita che il messaggio di errore suggerisce.
        if (!options.WindowEnabled || RichiestaSenzaFinestra(args)) return null;

        if (!string.IsNullOrWhiteSpace(options.TimeZoneId)) return null;

        return "Esecuzione in modalita' DELETE rifiutata: Purge:TimeZoneId non e' " +
               $"configurato, e la finestra {options.WindowStart}-{options.WindowEnd} " +
               $"verrebbe letta nel fuso della macchina ({TimeZoneInfo.Local.Id}). " +
               $"Dichiarare il fuso, oppure usare {NoWindowFlag} se rinunciare alla " +
               "finestra e' una scelta consapevole.";
    }

    private static bool RichiestaSenzaFinestra(IReadOnlyList<string> args) =>
        args.Any(a => a.Equals(NoWindowFlag, StringComparison.OrdinalIgnoreCase));
}
