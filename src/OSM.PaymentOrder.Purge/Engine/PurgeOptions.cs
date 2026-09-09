using System.ComponentModel.DataAnnotations;
using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine;

public sealed class PurgeOptions
{
    public const string SectionName = "Purge";

    // ---------------------------------------------------------------- soglie

    /// <summary>Anni di conservazione. Da confermare (PA-4).</summary>
    [Range(1, 30)]
    public int RetentionYears { get; set; } = 5;

    /// <summary>
    /// Come si calcola la soglia (PA-3). Il default è FiscalYearEnd perché è
    /// la lettura prudente: conserva di più, e l'errore che produce è
    /// recuperabile con un run successivo, mentre cancellare troppo presto no.
    /// </summary>
    public RetentionAnchorMode AnchorMode { get; set; } = RetentionAnchorMode.FiscalYearEnd;

    /// <summary>
    /// Soglia dedicata agli ordini abbandonati (§10.5, PA-21). Va tarata sulla
    /// distribuzione reale dell'età: PartiallyAuthorised non è una bozza, ha già
    /// almeno una firma, e una soglia troppo breve cancellerebbe ordini che
    /// stavano aspettando il secondo firmatario.
    /// </summary>
    [Range(1, 120)]
    public int AbandonedRetentionMonths { get; set; } = 24;

    /// <summary>
    /// Attiva lo sfoltimento degli abbandoni. Disattivato finché PA-21 è aperto.
    /// </summary>
    public bool AbandonedEnabled { get; set; }

    /// <summary>
    /// Righe esaminate per pagina in selezione ed espansione.
    ///
    /// Sotto la soglia di lock escalation di SQL Server (circa 5.000 lock per
    /// statement): la pagina legge da Order, e un lock condiviso escalato a
    /// livello di tabella bloccherebbe l'operativita', che e' precisamente
    /// cio' che il resto del motore evita.
    ///
    /// Alzarlo riduce il numero di andate e ritorno, abbassarlo riduce la
    /// durata del singolo statement. Il valore va tarato osservando le
    /// pagine registrate nel log di selezione.
    /// </summary>
    [Range(500, 50000)]
    public int SelectionBatchSize { get; set; } = 4000;

    // ------------------------------------------------------------ slicing

    /// <summary>
    /// Tetto sulle righe per slice. Protegge dalla lock escalation di SQL Server
    /// (~5.000 lock per statement). Da tarare sulla distribuzione p99 delle
    /// revisioni per ordine.
    /// </summary>
    [Range(100, 20000)]
    public int MaxRowsPerBatch { get; set; } = 3000;

    /// <summary>
    /// Tetto sul numero di ordini per slice. Protegge dal caso opposto: molte
    /// centinaia di ordini leggeri che producono comunque un numero elevato di
    /// lock e di statement in una sola transazione.
    /// </summary>
    [Range(10, 10000)]
    public int MaxOrdersPerBatch { get; set; } = 500;

    // ---------------------------------------------------------- esecuzione

    [Range(1, 10)]
    public int MaxSliceAttempts { get; set; } = 3;

    /// <summary>
    /// Quante volte un run puo' essere interrotto da un guasto e ripreso prima
    /// di essere dichiarato fallito.
    ///
    /// Un guasto non chiude il run: la fase resta il checkpoint da cui
    /// riprendere. Senza un limite, pero', un problema che non passa — un disco
    /// pieno, una credenziale scaduta — farebbe ripartire lo stesso run ogni
    /// notte per sempre, sempre con lo stesso esito e senza che nessuno se ne
    /// accorga. Superata la soglia il run diventa Failed e chiede attenzione.
    /// </summary>
    [Range(1, 50)]
    public int MaxRunInterruptions { get; set; } = 5;

    /// <summary>
    /// Quante volte una slice puo' essere divisa in due prima che il
    /// coordinatore rinunci e la abbandoni (D-11).
    ///
    /// La bisezione isola un aggregato che rifiuta la cancellazione in circa
    /// 2·log2(N) transazioni invece di abbandonarne N. Il limite esiste per
    /// il caso opposto: un difetto che sembra un errore di dati e non lo e'
    /// fallirebbe ogni figlia, e senza freno il lavoro sprecato sarebbe
    /// 2·N transazioni per scoprire una cosa sola. Con 2^10 > MaxOrdersPerBatch
    /// massimo, il default arriva sempre fino al singolo aggregato. Zero
    /// disattiva la bisezione e ripristina l'abbandono in blocco.
    /// </summary>
    [Range(0, 20)]
    public int MaxSplitDepth { get; set; } = 10;

    /// <summary>
    /// Peso oltre il quale un aggregato non viene cancellato automaticamente,
    /// ma escluso e censito. Zero disattiva il limite.
    ///
    /// MaxRowsPerBatch e' il tetto della slice, ma un aggregato che lo supera
    /// da solo diventa una slice dedicata — e finora senza limite superiore.
    /// Un collettivo da duecento componenti con molte revisioni puo' pesare
    /// centomila righe: una transazione che innesca la lock escalation e
    /// gonfia il log, cioe' esattamente cio' che il packing esiste per
    /// evitare. Sopra questa soglia l'aggregato resta a database e compare nel
    /// censimento, come i collettivi anomali di D-10.
    ///
    /// Il default e' dieci volte MaxRowsPerBatch: alto abbastanza da non
    /// scattare mai sui dati normali, basso abbastanza da intercettare il caso
    /// patologico. Va tarato sul p99 reale dopo il primo dry-run.
    /// </summary>
    [Range(0, 10_000_000)]
    public int MaxAggregateWeight { get; set; } = 30_000;

    public TimeSpan InterSliceDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    [Range(30, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 300;

    // ---------------------------------------------------------------- audit

    /// <summary>
    /// Produce il conteggio previsionale anche per i run reali, non solo per i
    /// dry-run. E' cio' che rende confrontabile Purge.vDryRunVsActual: senza,
    /// la view ha solo il previsto dei dry-run e l'effettivo dei run reali,
    /// che hanno RunId diversi e non si incontrano mai.
    ///
    /// Costa una passata di conteggi sullo staging prima dell'esecuzione.
    /// Disattivarlo solo se quella passata risulta insostenibile sui volumi
    /// reali, accettando di perdere il riscontro a posteriori.
    /// </summary>
    public bool AuditBaselineEnabled { get; set; } = true;

    // ---------------------------------------------------------- housekeeping

    /// <summary>
    /// Sfoltimento delle tabelle di controllo del purge. Lo staging cresce in
    /// proporzione ai dati cancellati e su volumi reali puo' superarli.
    /// </summary>
    public bool HousekeepingEnabled { get; set; } = true;

    /// <summary>
    /// Giorni di conservazione dello staging dei run conclusi senza incidenti.
    /// Dato di lavoro esaurito: la finestra serve solo a permettere un'ispezione
    /// a posteriori nei giorni immediatamente successivi.
    /// </summary>
    [Range(0, 3650)]
    public int StagingRetentionDays { get; set; } = 7;

    /// <summary>
    /// Giorni di conservazione dello staging dei run falliti o con slice
    /// abbandonate. Finestra molto piu' lunga: quello staging e' l'unico
    /// appiglio per capire quali aggregati hanno dato problemi.
    /// </summary>
    [Range(1, 3650)]
    public int FailedStagingRetentionDays { get; set; } = 90;

    [Range(100, 10000)]
    public int HousekeepingBatchSize { get; set; } = 4000;

    /// <summary>
    /// Run trattati per ciclo. Limita la durata della pulizia quando esiste un
    /// arretrato: meglio ripulire un po' ogni notte che occupare l'intera
    /// finestra al primo avvio dopo l'introduzione della funzione.
    /// </summary>
    [Range(1, 1000)]
    public int HousekeepingMaxRunsPerCycle { get; set; } = 50;

    // -------------------------------------------------------------- finestra

    /// <summary>
    /// Fuso orario in cui vanno letti WindowStart e WindowEnd. Null significa
    /// il fuso dell'host.
    ///
    /// Va valorizzato in produzione. In un container TZ non e' impostata e il
    /// fuso locale e' UTC: "01:00-05:00" diventa 02:00-06:00 italiane
    /// d'inverno e 03:00-07:00 d'estate. Il purge girerebbe nell'ora sbagliata
    /// tutte le notti, senza errori e senza avvisi — se ne accorgerebbe solo
    /// chi nota che il carico arriva a operativita' gia' avviata.
    ///
    /// Accetta sia gli identificativi Windows ("W. Europe Standard Time") sia
    /// quelli IANA ("Europe/Rome"): da .NET 8 sono equivalenti su entrambe le
    /// piattaforme. Un identificativo sconosciuto solleva all'avvio, che e'
    /// il momento giusto per accorgersene.
    /// </summary>
    public string? TimeZoneId { get; set; }

    private TimeZoneInfo? _timeZone;

    /// <summary>Fuso risolto, con il fuso dell'host come ripiego.</summary>
    public TimeZoneInfo TimeZone => _timeZone ??=
        string.IsNullOrWhiteSpace(TimeZoneId)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>
    /// Ora corrente nel fuso della finestra. Da usare ovunque al posto di
    /// TimeProvider.GetLocalNow(): quest'ultima dipende dall'host, questa da
    /// una configurazione dichiarata.
    /// </summary>
    public DateTimeOffset Now(TimeProvider clock) =>
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZone);

    public bool WindowEnabled { get; set; } = true;
    public TimeOnly WindowStart { get; set; } = new(1, 0);
    public TimeOnly WindowEnd { get; set; } = new(5, 0);

    /// <summary>
    /// Tolleranza oltre la chiusura della finestra, scaduta la quale le fasi
    /// lunghe vengono interrotte d'autorita'.
    ///
    /// Non e' il meccanismo normale di chiusura: il coordinatore smette da se'
    /// di prendere slice a fine finestra, e lo fa restituendo un esito, non
    /// sollevando un'eccezione. La tolleranza esiste per il caso che quel
    /// controllo non copre — una selezione, un'espansione o un planning ancora
    /// in corso alle cinque del mattino — e scadere e' di per se' un'anomalia
    /// da segnalare, non un evento previsto.
    ///
    /// Troppo corta interromperebbe la chiusura ordinata di una slice appena
    /// iniziata; troppo lunga rimanderebbe il problema dentro l'orario
    /// lavorativo, che e' cio' che si vuole evitare.
    /// </summary>
    public TimeSpan WindowGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Espressione cron del risveglio. Il default corrisponde all'una di notte.
    /// La finestra resta il vero limite: il cron decide quando iniziare.
    /// </summary>
    public string CronExpression { get; set; } = "0 1 * * *";

    /// <summary>
    /// Se true, il motore produce solo il report e non cancella nulla.
    /// Va mantenuto attivo finché il report non è stato approvato, e
    /// preferibilmente legato all'ambiente anziché al solo file di
    /// configurazione: una modifica fatta in sviluppo che finisse in
    /// produzione farebbe cancellare davvero al primo run notturno.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Strategie da eseguire, nell'ordine.</summary>
    public List<RetentionStrategy> Strategies { get; set; } =
    [
        RetentionStrategy.Terminated,
        RetentionStrategy.StandingOrders,
        RetentionStrategy.Collective,
        RetentionStrategy.OrphanHistory
    ];

    // ---------------------------------------------------------------- calcoli

    /// <summary>Trasforma i parametri configurati nella policy di retention.</summary>
    public RetentionPolicy ToRetentionPolicy() =>
        new(RetentionYears, AnchorMode, AbandonedRetentionMonths);

    /// <summary>
    /// Soglia di retention. Le due letture non sono equivalenti: per un'operazione
    /// del 3 gennaio lo scarto è di quasi dodici mesi.
    /// </summary>
    public DateTime ComputeRetentionCutoff(DateTimeOffset reference) =>
        ToRetentionPolicy().ComputeRetentionCutoff(reference);

    /// <summary>La finestra dei falliti deve essere almeno quella dei conclusi.</summary>
    public bool HousekeepingWindowsAreConsistent =>
        FailedStagingRetentionDays >= StagingRetentionDays;

    public DateTime ComputeAbandonedCutoff(DateTimeOffset reference) =>
        ToRetentionPolicy().ComputeAbandonedCutoff(reference);

    public bool IsWithinWindow(DateTimeOffset now)
    {
        if (!WindowEnabled) return true;
        var t = TimeOnly.FromDateTime(now.DateTime);
        return WindowStart <= WindowEnd
            ? t >= WindowStart && t < WindowEnd
            : t >= WindowStart || t < WindowEnd;   // finestra a cavallo della mezzanotte
    }

    /// <summary>
    /// Quanto manca alla chiusura della finestra. Null se la finestra non e'
    /// attiva, zero se e' gia' chiusa.
    ///
    /// Serve a costruire la scadenza oltre la quale le fasi lunghe vengono
    /// interrotte. Il coordinatore delle slice continua a fermarsi da se' a
    /// fine finestra: questo calcolo copre le fasi che quel controllo non ce
    /// l'hanno — selezione, espansione, validazione, planning.
    /// </summary>
    public TimeSpan? TimeUntilWindowEnd(DateTimeOffset now)
    {
        if (!WindowEnabled) return null;
        if (!IsWithinWindow(now)) return TimeSpan.Zero;

        // Si costruisce l'ISTANTE di chiusura e si sottrae, invece di
        // sottrarre due orari.
        //
        // La differenza conta due volte l'anno. Il timer che nasce da questo
        // valore misura tempo reale; la sottrazione fra due TimeOnly misura
        // orologio, e nelle notti di cambio ora le due cose divergono di
        // un'ora. Nella notte di primavera la scadenza risultava piu'
        // generosa di sessanta minuti, e una fase lunga poteva proseguire
        // dentro l'orario lavorativo: esattamente cio' che la finestra esiste
        // per impedire.
        var local = now.DateTime;
        var oggi = DateOnly.FromDateTime(local);
        var adesso = TimeOnly.FromDateTime(local);

        // Se la chiusura e' gia' passata sull'orologio di oggi ma siamo dentro
        // la finestra, allora la finestra e' a cavallo della mezzanotte e la
        // chiusura cade domani.
        var giornoChiusura = adesso < WindowEnd ? oggi : oggi.AddDays(1);

        var chiusura = ToInstant(giornoChiusura.ToDateTime(WindowEnd));
        var residuo = chiusura - now;

        return residuo > TimeSpan.Zero ? residuo : TimeSpan.Zero;
    }

    /// <summary>
    /// Istante corrispondente a un orario locale nel fuso della finestra,
    /// gestendo i due casi patologici del cambio ora.
    /// </summary>
    private DateTimeOffset ToInstant(DateTime local)
    {
        // Ora inesistente (salto di primavera): l'orario configurato non
        // esiste in questa data. Si prende il primo istante valido dopo.
        for (var i = 0; i < 8 && TimeZone.IsInvalidTime(local); i++)
            local = local.AddMinutes(15);

        if (TimeZone.IsAmbiguousTime(local))
        {
            // Ora doppia (ritorno all'ora solare): due istanti corrispondono a
            // questo orario. Si sceglie il primo, cioe' l'offset maggiore.
            // Chiudere in anticipo costa un'ora di lavoro non fatto; chiudere
            // in ritardo porta il purge nell'orario lavorativo.
            return new DateTimeOffset(local, TimeZone.GetAmbiguousTimeOffsets(local).Max());
        }

        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }
}
