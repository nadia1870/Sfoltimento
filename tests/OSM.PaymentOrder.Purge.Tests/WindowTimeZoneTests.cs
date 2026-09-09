using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-16: la finestra operativa in un fuso dichiarato, e la scadenza calcolata
/// come istante invece che come differenza di orologio.
///
/// I test usano Europe/Rome perche' il cambio ora e' quello che interessa;
/// l'identificativo IANA funziona anche su Windows da .NET 8.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WindowTimeZoneTests
{
    private static readonly TimeZoneInfo Roma = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    private static PurgeOptions Opzioni() => new()
    {
        TimeZoneId = "Europe/Rome",
        WindowEnabled = true,
        WindowStart = new TimeOnly(1, 0),
        WindowEnd = new TimeOnly(5, 0)
    };

    /// <summary>Istante UTC corrispondente a un orario di Roma.</summary>
    private static DateTimeOffset Ora(int anno, int mese, int giorno, int h, int m)
    {
        var local = new DateTime(anno, mese, giorno, h, m, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Roma.GetUtcOffset(local));
    }

    private static DateTimeOffset InFuso(PurgeOptions o, DateTimeOffset istante) =>
        TimeZoneInfo.ConvertTime(istante, o.TimeZone);

    [Fact]
    public void Una_notte_normale_conta_il_tempo_che_manca()
    {
        var o = Opzioni();

        var residuo = o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 2, 10, 1, 30)));

        Assert.Equal(TimeSpan.FromHours(3.5), residuo);
    }

    /// <summary>
    /// Notte del passaggio all'ora legale: alle 02:00 l'orologio salta alle
    /// 03:00. Dall'01:30 alle 05:00 di orologio passano **due ore e mezza** di
    /// tempo reale, non tre e mezza.
    ///
    /// E' il caso che il calcolo per differenza di orari sbagliava, e nella
    /// direzione peggiore: il timer, che misura tempo reale, sarebbe scaduto
    /// alle 06:00 di orologio, un'ora dentro l'orario lavorativo.
    /// </summary>
    [Fact]
    public void La_notte_di_primavera_non_regala_un_ora()
    {
        var o = Opzioni();

        var residuo = o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 3, 29, 1, 30)));

        Assert.Equal(TimeSpan.FromHours(2.5), residuo);
    }

    /// <summary>
    /// Notte del ritorno all'ora solare: alle 03:00 l'orologio torna alle
    /// 02:00, quindi fino alle 05:00 di orologio passano quattro ore e mezza.
    /// Qui l'errore precedente era innocuo — si chiudeva un'ora prima — ma il
    /// conto giusto e' comunque questo.
    /// </summary>
    [Fact]
    public void La_notte_d_autunno_conta_l_ora_in_piu()
    {
        var o = Opzioni();

        var residuo = o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 10, 25, 1, 30)));

        Assert.Equal(TimeSpan.FromHours(4.5), residuo);
    }

    /// <summary>
    /// Una finestra che chiude nell'ora doppia: 02:30 esiste due volte nella
    /// notte d'autunno. Si sceglie la prima occorrenza — chiudere in anticipo
    /// costa lavoro non fatto, chiudere in ritardo porta il purge
    /// nell'operativita'.
    /// </summary>
    [Fact]
    public void Con_chiusura_nell_ora_doppia_si_sceglie_la_prima()
    {
        var o = Opzioni();
        o.WindowEnd = new TimeOnly(2, 30);

        var residuo = o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 10, 25, 1, 30)));

        Assert.Equal(TimeSpan.FromHours(1), residuo);
    }

    /// <summary>
    /// Una finestra che chiude in un orario che nella notte di primavera non
    /// esiste: 02:30 viene saltato. Non deve sollevare, e la chiusura cade al
    /// primo istante valido.
    /// </summary>
    [Fact]
    public void Con_chiusura_in_un_ora_inesistente_non_solleva()
    {
        var o = Opzioni();
        o.WindowEnd = new TimeOnly(2, 30);

        var residuo = o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 3, 29, 1, 30)));

        Assert.NotNull(residuo);
        Assert.InRange(residuo!.Value, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    /// <summary>La finestra a cavallo della mezzanotte resta corretta.</summary>
    [Fact]
    public void La_finestra_a_cavallo_della_mezzanotte_conta_oltre_il_giorno()
    {
        var o = Opzioni();
        o.WindowStart = new TimeOnly(23, 0);
        o.WindowEnd = new TimeOnly(3, 0);

        Assert.Equal(TimeSpan.FromHours(4), o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 2, 10, 23, 0))));
        Assert.Equal(TimeSpan.FromHours(1), o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 2, 11, 2, 0))));
    }

    [Fact]
    public void Fuori_finestra_il_residuo_e_zero_e_disattivata_e_nullo()
    {
        var o = Opzioni();

        Assert.Equal(TimeSpan.Zero, o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 2, 10, 9, 0))));

        o.WindowEnabled = false;
        Assert.Null(o.TimeUntilWindowEnd(InFuso(o, Ora(2026, 2, 10, 1, 30))));
    }

    /// <summary>
    /// Il fuso non dichiarato ripiega su quello dell'host: e' il
    /// comportamento precedente, conservato come default ma non piu' implicito
    /// — Program lo scrive nel log all'avvio.
    /// </summary>
    [Fact]
    public void Senza_TimeZoneId_si_usa_il_fuso_dell_host()
    {
        Assert.Equal(TimeZoneInfo.Local, new PurgeOptions().TimeZone);
    }

    /// <summary>
    /// Senza un fuso dichiarato il calcolo torna a essere una differenza di
    /// orologio, e conserva l'offset dell'istante ricevuto.
    ///
    /// E' il caso che ha fatto fallire la CI mentre in locale era verde: la
    /// prima versione usava TimeZoneInfo.Local, che su una macchina di
    /// sviluppo in Europe/Rome coincide con l'offset dei dati di prova e su un
    /// runner in UTC no. Un calcolo che dipende dalla macchina su cui gira e'
    /// esattamente il difetto che D-16 corregge, e rimetterlo nel calcolo
    /// della scadenza sarebbe stato spostarlo, non risolverlo.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-5)]
    public void Senza_fuso_dichiarato_il_residuo_non_dipende_dalla_macchina(int offsetOre)
    {
        var o = new PurgeOptions
        {
            WindowEnabled = true,
            WindowStart = new TimeOnly(1, 0),
            WindowEnd = new TimeOnly(5, 0)
        };

        var adesso = new DateTimeOffset(2026, 9, 8, 3, 0, 0, TimeSpan.FromHours(offsetOre));

        Assert.Equal(TimeSpan.FromHours(2), o.TimeUntilWindowEnd(adesso));
    }

    [Fact]
    public void Un_fuso_sconosciuto_solleva_invece_di_ripiegare()
    {
        var o = new PurgeOptions { TimeZoneId = "Fuso/Inesistente" };

        Assert.ThrowsAny<TimeZoneNotFoundException>(() => o.TimeZone);
    }

    /// <summary>
    /// Now() legge l'orologio in UTC e converte: non dipende dal fuso
    /// dell'host, che nei container e' quasi sempre UTC.
    /// </summary>
    [Fact]
    public void Now_converte_nel_fuso_configurato()
    {
        var o = Opzioni();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        var adesso = o.Now(clock);

        Assert.Equal(new TimeOnly(14, 0), TimeOnly.FromDateTime(adesso.DateTime));  // CEST = UTC+2
    }

    /// <summary>
    /// Senza fuso dichiarato Now() delega al TimeProvider, che conosce il
    /// proprio: un orologio finto con un fuso proprio deve essere rispettato,
    /// altrimenti i test tornano a dipendere dalla macchina.
    /// </summary>
    [Fact]
    public void Senza_fuso_dichiarato_Now_usa_quello_del_TimeProvider()
    {
        var o = new PurgeOptions();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero))
        {
            Zona = TimeZoneInfo.CreateCustomTimeZone("Prova+3", TimeSpan.FromHours(3), "Prova+3", "Prova+3")
        };

        Assert.Equal(new TimeOnly(15, 0), TimeOnly.FromDateTime(o.Now(clock).DateTime));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public TimeZoneInfo Zona { get; init; } = TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => Zona;
    }
}
