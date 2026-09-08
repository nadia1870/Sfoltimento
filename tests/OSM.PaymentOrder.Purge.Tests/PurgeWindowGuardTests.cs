using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La finestra operativa decide quando smettere, non solo quando iniziare.
/// Prima era applicata alle sole slice: le fasi lunghe proseguivano nella
/// mattina lavorativa senza che nulla le fermasse.
///
/// L'aritmetica sta in PurgeOptions ed e' testabile da sola. Della scadenza si
/// verifica il valore calcolato all'apertura, non lo scatto del timer: farlo
/// scattare davvero significherebbe un test che aspetta, cioe' un test lento e
/// prima o poi instabile.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PurgeWindowGuardTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static PurgeOptions Opzioni(
        bool enabled = true,
        int startHour = 1,
        int endHour = 5) =>
        new()
        {
            WindowEnabled = enabled,
            WindowStart = new TimeOnly(startHour, 0),
            WindowEnd = new TimeOnly(endHour, 0),
            WindowGrace = Grace
        };

    private static DateTimeOffset Alle(int ora, int minuti = 0) =>
        new(2026, 9, 8, ora, minuti, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Finestra_disattivata_non_ha_scadenza()
    {
        Assert.Null(Opzioni(enabled: false).TimeUntilWindowEnd(Alle(3)));
    }

    [Fact]
    public void Dentro_la_finestra_il_residuo_e_il_tempo_alla_chiusura()
    {
        Assert.Equal(TimeSpan.FromHours(2), Opzioni().TimeUntilWindowEnd(Alle(3)));
    }

    [Fact]
    public void Fuori_dalla_finestra_il_residuo_e_zero()
    {
        Assert.Equal(TimeSpan.Zero, Opzioni().TimeUntilWindowEnd(Alle(9)));
    }

    /// <summary>
    /// L'istante di chiusura e' escluso da IsWithinWindow: alle cinque in punto
    /// si e' gia' fuori, e il residuo non deve diventare ventiquattr'ore.
    /// </summary>
    [Fact]
    public void Allistante_di_chiusura_il_residuo_e_zero()
    {
        Assert.Equal(TimeSpan.Zero, Opzioni().TimeUntilWindowEnd(Alle(5)));
    }

    [Theory]
    // finestra 22:00-02:00, prima della mezzanotte
    [InlineData(23, 3)]
    // stessa finestra, dopo la mezzanotte
    [InlineData(1, 1)]
    public void Finestra_a_cavallo_della_mezzanotte(int ora, int oreAttese)
    {
        var opzioni = Opzioni(startHour: 22, endHour: 2);

        Assert.Equal(
            TimeSpan.FromHours(oreAttese),
            opzioni.TimeUntilWindowEnd(Alle(ora)));
    }

    [Fact]
    public void Senza_finestra_il_token_e_quello_del_chiamante()
    {
        using var esterno = new CancellationTokenSource();
        using var scope = Guardiano(Opzioni(enabled: false), Alle(9)).Open(esterno.Token);

        Assert.Null(scope.Deadline);
        Assert.False(scope.Token.IsCancellationRequested);
        Assert.False(scope.ClosedByWindow);

        esterno.Cancel();

        Assert.True(scope.Token.IsCancellationRequested);

        // Uno spegnimento non e' uno sforamento: il chiamante deve poter
        // distinguere i due casi dentro lo stesso catch.
        Assert.False(scope.ClosedByWindow);
    }

    [Fact]
    public void La_scadenza_e_il_residuo_piu_la_tolleranza()
    {
        using var esterno = new CancellationTokenSource();
        using var scope = Guardiano(Opzioni(), Alle(3)).Open(esterno.Token);

        Assert.Equal(TimeSpan.FromHours(2) + Grace, scope.Deadline);
        Assert.False(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public void Lo_spegnimento_cancella_anche_con_la_finestra_attiva()
    {
        using var esterno = new CancellationTokenSource();
        using var scope = Guardiano(Opzioni(), Alle(3)).Open(esterno.Token);

        esterno.Cancel();

        Assert.True(scope.Token.IsCancellationRequested);
        Assert.False(scope.ClosedByWindow);
    }

    /// <summary>
    /// Tolleranza nulla e finestra gia' chiusa: la scadenza e' zero, quindi il
    /// token scatta subito. E' il solo modo di esercitare il ramo dello
    /// sforamento senza costruire un test che aspetta davvero.
    ///
    /// L'attesa e' limitata e non fissa: se il timer scatta in modo sincrono
    /// alla costruzione il test non aspetta nulla, e se scatta sul threadpool
    /// non diventa instabile per qualche millisecondo di scarto.
    /// </summary>
    [Fact]
    public void Scadenza_gia_trascorsa_fa_scattare_la_finestra()
    {
        var opzioni = Opzioni();
        opzioni.WindowGrace = TimeSpan.Zero;

        using var esterno = new CancellationTokenSource();
        using var scope = Guardiano(opzioni, Alle(9)).Open(esterno.Token);

        Assert.Equal(TimeSpan.Zero, scope.Deadline);
        Assert.True(scope.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.True(scope.ClosedByWindow);
    }

    private static PurgeWindowGuard Guardiano(PurgeOptions opzioni, DateTimeOffset adesso) =>
        new(Options.Create(opzioni),
            new OrologioFisso(adesso),
            NullLogger<PurgeWindowGuard>.Instance);

    private sealed class OrologioFisso(DateTimeOffset localNow) : TimeProvider
    {
        private static readonly TimeZoneInfo Fuso =
            TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test +02", "Test +02");

        public override TimeZoneInfo LocalTimeZone => Fuso;

        public override DateTimeOffset GetUtcNow() => localNow.ToUniversalTime();
    }
}
