using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-20: il fuso è obbligatorio per le esecuzioni reali con la finestra
/// attiva.
///
/// Questi test verificano il comportamento, non il testo del sorgente. La
/// prima versione del controllo viveva dentro Program ed era coperta solo da
/// un controllo di collegamento (D-18): quel controllo confermava che la riga
/// esistesse e stesse nel posto giusto, ma non poteva accorgersi che la
/// condizione fosse sbagliata — e infatti non se ne è accorto. Il messaggio
/// di errore suggeriva --no-window come via d'uscita, mentre il flag veniva
/// letto più tardi, dentro RunOnceAsync: la via d'uscita era chiusa dalla
/// stessa guardia che la proponeva.
///
/// I test di collegamento restano utili, ma sono complementari a questi, non
/// sostitutivi.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StartupGuardTests
{
    private static PurgeOptions Opzioni(string? fuso, bool finestra = true) => new()
    {
        TimeZoneId = fuso,
        WindowEnabled = finestra,
        WindowStart = new TimeOnly(1, 0),
        WindowEnd = new TimeOnly(5, 0)
    };

    private static string[] Args(params string[] a) => a;

    /// <summary>Il caso che la decisione esiste per impedire.</summary>
    [Fact]
    public void Delete_con_finestra_e_senza_fuso_viene_rifiutato()
    {
        var motivo = PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.Delete, Opzioni(fuso: null), Args("once", "--delete"));

        Assert.NotNull(motivo);
        Assert.Contains("TimeZoneId", motivo);

        // Il messaggio deve dire quale fuso sarebbe stato usato: senza, chi
        // legge non sa se il default fosse innocuo o no.
        Assert.Contains(TimeZoneInfo.Local.Id, motivo);
    }

    [Fact]
    public void Delete_con_fuso_dichiarato_procede()
    {
        Assert.Null(PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.Delete, Opzioni("Europe/Rome"), Args("once", "--delete")));
    }

    /// <summary>
    /// Il difetto che questi test sono nati per fissare: la guardia proponeva
    /// --no-window come via d'uscita e insieme la rendeva impraticabile,
    /// perché il flag veniva letto dopo.
    /// </summary>
    [Fact]
    public void Delete_senza_fuso_ma_con_no_window_procede()
    {
        Assert.Null(PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.Delete, Opzioni(fuso: null),
            Args("once", "--delete", "--no-window")));
    }

    [Fact]
    public void Il_flag_no_window_non_distingue_maiuscole()
    {
        Assert.Null(PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.Delete, Opzioni(fuso: null),
            Args("once", "--delete", "--NO-WINDOW")));
    }

    /// <summary>Con la finestra disattivata in configurazione il fuso non governa nulla.</summary>
    [Fact]
    public void Delete_senza_fuso_e_con_finestra_disattivata_procede()
    {
        Assert.Null(PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.Delete, Opzioni(fuso: null, finestra: false),
            Args("once", "--delete")));
    }

    /// <summary>
    /// La simulazione resta permissiva in ogni combinazione: è il percorso che
    /// porta all'approvazione, e non cancella nulla.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("Europe/Rome", true)]
    public void Il_dry_run_non_viene_mai_rifiutato(string? fuso, bool finestra)
    {
        Assert.Null(PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.DryRun, Opzioni(fuso, finestra), Args("once", "--dry-run")));
    }

    /// <summary>Una stringa di soli spazi non è un fuso dichiarato.</summary>
    [Fact]
    public void Un_fuso_vuoto_vale_come_assente()
    {
        Assert.NotNull(PurgeStartupGuard.RejectionReason(
            PurgeExecutionMode.Delete, Opzioni("   "), Args("once", "--delete")));
    }
}
