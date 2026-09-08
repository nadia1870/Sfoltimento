namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Cosa si sta autorizzando in questa esecuzione.
///
/// La modalita' arriva dalla riga di comando, non dalla configurazione. In
/// produzione UC4 espone due job distinti — PURGE_DRY_RUN e PURGE_DELETE — e
/// la modalita' e' una proprieta' del job, non qualcosa che un operatore
/// modifica ogni volta.
///
/// La divisione e' netta: la configurazione dice *come* eseguire il purge
/// (dimensione delle pagine, finestra, tentativi), la riga di comando dice
/// *cosa si e' autorizzati a fare adesso*. Un appsettings copiato per sbaglio
/// non puo' piu' trasformare una simulazione in una cancellazione.
/// </summary>
public enum PurgeExecutionMode
{
    DryRun,
    Delete
}

/// <summary>
/// Riconosce la modalita' dagli argomenti. L'assenza non ha un default: un
/// comando senza modalita' non esegue niente.
/// </summary>
public static class PurgeExecutionModeParser
{
    public const string DryRunFlag = "--dry-run";
    public const string DeleteFlag = "--delete";

    /// <summary>
    /// Null significa "non specificata" oppure "contraddittoria". In entrambi
    /// i casi il chiamante deve rifiutarsi di eseguire: indovinare
    /// l'intenzione, su un comando che cancella dati, e' esattamente cio' che
    /// non si deve fare.
    /// </summary>
    public static PurgeExecutionMode? Parse(string[] args)
    {
        var dryRun = args.Any(a => a.Equals(DryRunFlag, StringComparison.OrdinalIgnoreCase));
        var delete = args.Any(a => a.Equals(DeleteFlag, StringComparison.OrdinalIgnoreCase));

        if (dryRun == delete) return null;   // nessuna delle due, o entrambe

        return delete ? PurgeExecutionMode.Delete : PurgeExecutionMode.DryRun;
    }

    public const string Usage =
        "Specificare la modalita' di esecuzione: " + DryRunFlag + " oppure " + DeleteFlag + ".";
}
