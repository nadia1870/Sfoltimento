using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Primo livello: la modalita' arriva dalla riga di comando e non ha default.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExecutionModeTests
{
    [Theory]
    [InlineData("once", "--dry-run")]
    [InlineData("--dry-run")]
    [InlineData("once", "Terminated", "--dry-run")]
    public void Il_flag_dry_run_seleziona_la_simulazione(params string[] args) =>
        Assert.Equal(PurgeExecutionMode.DryRun, PurgeExecutionModeParser.Parse(args));

    [Theory]
    [InlineData("once", "--delete")]
    [InlineData("once", "Collective", "--delete")]
    public void Il_flag_delete_seleziona_l_esecuzione_reale(params string[] args) =>
        Assert.Equal(PurgeExecutionMode.Delete, PurgeExecutionModeParser.Parse(args));

    /// <summary>
    /// Nessuna modalita' non significa "simula per prudenza": significa non
    /// eseguire. Un default silenzioso, anche prudente, riporterebbe la
    /// decisione fuori dalla riga di comando.
    /// </summary>
    [Theory]
    [InlineData]
    [InlineData("once")]
    [InlineData("once", "Terminated")]
    public void Senza_modalita_non_si_esegue(params string[] args) =>
        Assert.Null(PurgeExecutionModeParser.Parse(args));

    /// <summary>
    /// Entrambi i flag e' una contraddizione, non una precedenza da stabilire.
    /// Su un comando che cancella dati, indovinare l'intenzione e' la cosa da
    /// non fare.
    /// </summary>
    [Fact]
    public void Entrambi_i_flag_sono_un_rifiuto() =>
        Assert.Null(PurgeExecutionModeParser.Parse(["once", "--dry-run", "--delete"]));

    [Fact]
    public void Il_riconoscimento_non_dipende_dalle_maiuscole() =>
        Assert.Equal(PurgeExecutionMode.Delete,
            PurgeExecutionModeParser.Parse(["once", "--DELETE"]));
}

/// <summary>
/// L'impronta della policy: cambia con cio' che determina l'insieme dei
/// candidati, resta uguale quando cambiano le manopole di prestazione.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PurgePolicyTests
{
    private static PurgeOptions Base() => new()
    {
        RetentionYears = 5,
        AnchorMode = RetentionAnchorMode.FiscalYearEnd,
        AbandonedEnabled = false,
        AbandonedRetentionMonths = 24,
        Strategies = [RetentionStrategy.Terminated, RetentionStrategy.Collective]
    };

    [Fact]
    public void La_stessa_policy_produce_la_stessa_impronta() =>
        Assert.Equal(PurgePolicy.ComputeHash(Base()), PurgePolicy.ComputeHash(Base()));

    /// <summary>
    /// L'ordine in cui le strategie compaiono in configurazione non e' una
    /// differenza di policy: riordinarle non deve invalidare un'approvazione.
    /// </summary>
    [Fact]
    public void L_ordine_delle_strategie_non_cambia_l_impronta()
    {
        var a = Base();
        var b = Base();
        b.Strategies = [RetentionStrategy.Collective, RetentionStrategy.Terminated];

        Assert.Equal(PurgePolicy.ComputeHash(a), PurgePolicy.ComputeHash(b));
    }

    [Fact]
    public void Cambiare_gli_anni_di_retention_cambia_l_impronta()
    {
        var a = Base();
        var b = Base();
        b.RetentionYears = 2;

        Assert.NotEqual(PurgePolicy.ComputeHash(a), PurgePolicy.ComputeHash(b));
    }

    [Fact]
    public void Cambiare_l_ancoraggio_cambia_l_impronta()
    {
        var a = Base();
        var b = Base();
        b.AnchorMode = RetentionAnchorMode.RollingDate;

        Assert.NotEqual(PurgePolicy.ComputeHash(a), PurgePolicy.ComputeHash(b));
    }

    [Fact]
    public void Aggiungere_una_strategia_cambia_l_impronta()
    {
        var a = Base();
        var b = Base();
        b.Strategies = [.. a.Strategies, RetentionStrategy.StandingOrders];

        Assert.NotEqual(PurgePolicy.ComputeHash(a), PurgePolicy.ComputeHash(b));
    }

    [Fact]
    public void Attivare_gli_abbandonati_cambia_l_impronta()
    {
        var a = Base();
        var b = Base();
        b.AbandonedEnabled = true;

        Assert.NotEqual(PurgePolicy.ComputeHash(a), PurgePolicy.ComputeHash(b));
    }

    /// <summary>
    /// Le manopole di prestazione restano fuori. Tararle dopo il collaudo non
    /// deve invalidare un'approvazione e bloccare il job notturno per una
    /// modifica che non cambia cosa viene cancellato.
    /// </summary>
    [Fact]
    public void Le_manopole_di_prestazione_non_cambiano_l_impronta()
    {
        var a = Base();
        var b = Base();
        b.SelectionBatchSize = 20000;
        b.MaxRowsPerBatch = 500;
        b.MaxSliceAttempts = 7;
        b.WindowEnabled = false;
        b.HousekeepingEnabled = false;
        b.CommandTimeoutSeconds = 60;

        Assert.Equal(PurgePolicy.ComputeHash(a), PurgePolicy.ComputeHash(b));
    }

    /// <summary>
    /// Chi approva deve vedere cosa approva: la descrizione deve contenere i
    /// numeri, non solo i nomi dei campi.
    /// </summary>
    [Fact]
    public void La_descrizione_riporta_i_valori_della_policy()
    {
        var testo = PurgePolicy.Describe(Base());

        Assert.Contains("5", testo);
        Assert.Contains("FiscalYearEnd", testo);
        Assert.Contains("Terminated", testo);
    }
}

/// <summary>
/// La modalita' servizio non ha una riga di comando per esecuzione, quindi non
/// deve poter cancellare: e' dry-run per costruzione.
///
/// Questi test riproducono la decisione presa in Program invece di invocare il
/// processo: il comportamento che conta e' quale modalita' viene scelta, e
/// tenerlo qui lo rende verificabile nel job unitario.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ServiceModeTests
{
    /// <summary>Cosa decide Program: null significa rifiuto di partire.</summary>
    private static PurgeExecutionMode? Decidi(bool once, string[] args)
    {
        if (!once)
        {
            return PurgeExecutionModeParser.Parse(args) == PurgeExecutionMode.Delete
                ? null
                : PurgeExecutionMode.DryRun;
        }

        return PurgeExecutionModeParser.Parse(args);
    }

    [Fact]
    public void Il_servizio_senza_flag_e_una_simulazione() =>
        Assert.Equal(PurgeExecutionMode.DryRun, Decidi(once: false, []));

    [Fact]
    public void Il_servizio_con_dry_run_esplicito_resta_una_simulazione() =>
        Assert.Equal(PurgeExecutionMode.DryRun, Decidi(once: false, ["--dry-run"]));

    /// <summary>
    /// Non degrada in silenzio: un servizio che si crede in cancellazione e
    /// invece simula e' lo stesso errore dell'uscita con zero senza aver fatto
    /// niente.
    /// </summary>
    [Fact]
    public void Il_servizio_con_delete_si_rifiuta_di_partire() =>
        Assert.Null(Decidi(once: false, ["--delete"]));

    [Fact]
    public void Solo_once_puo_cancellare() =>
        Assert.Equal(PurgeExecutionMode.Delete, Decidi(once: true, ["once", "--delete"]));
}
