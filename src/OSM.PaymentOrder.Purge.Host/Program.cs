using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Engine.Phases;
using OSM.PaymentOrder.Purge.Migrations;
using OSM.PaymentOrder.Purge.Observability;

namespace OSM.PaymentOrder.Purge.Host;

public static class Program
{
    /// <summary>
    /// Due modalita':
    ///   purge once --dry-run [strat]   simulazione, nessuna cancellazione
    ///   purge once --delete  [strat]   esecuzione reale
    ///   ... --no-window                ignora la chiusura della finestra
    ///   purge approve <run-id> --by <nome> [--note <testo>]
    ///   purge migrate [--status]       allinea lo schema Purge (D-13);
    ///                                  con --status elenca senza applicare
    ///   purge  (senza once)            servizio con cronjob interno,
    ///                                  dry-run per costruzione
    ///
    /// La modalita' e' obbligatoria e arriva dalla riga di comando, mai dalla
    /// configurazione. In produzione UC4 espone due job distinti,
    /// PURGE_DRY_RUN e PURGE_DELETE, cosi' la modalita' e' una proprieta' del
    /// job invece di un parametro che qualcuno modifica.
    ///
    /// Senza modalita' il comando non esegue niente ed esce con codice 2.
    /// Indovinare l'intenzione, su un comando che cancella dati, e'
    /// precisamente cio' che non si deve fare.
    ///
    /// La finestra operativa resta attiva anche in modalita' 'once'. Lo
    /// scheduler esterno decide quando partire; la configurazione continua a
    /// decidere quando smettere di prendere nuove slice. Per rinunciarvi serve
    /// --no-window, esplicito.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var comando = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

        if (comando == "approve")
        {
            using var approvazione = BuildHost(args, once: true);
            return await ApproveAsync(approvazione, args).ConfigureAwait(false);
        }

        if (comando == "migrate")
        {
            using var migrazione = BuildHost(args, once: true);
            return Migrate(migrazione, args);
        }

        var once = comando == "once";

        // La modalita' servizio e' dry-run per costruzione.
        //
        // Non ha una riga di comando per esecuzione: il cron interno decide da
        // se' quando partire, e nessuno autorizza le singole notti. Dare a
        // quel percorso la capacita' di cancellare significherebbe che la
        // modalita' torna a vivere in configurazione, cioe' esattamente il
        // buco che questi flag chiudono.
        //
        // Chiedere --delete qui non fa degradare in silenzio a simulazione: un
        // servizio che si crede in cancellazione e invece simula e' lo stesso
        // errore dell'uscita con codice zero senza aver fatto niente. Si
        // rifiuta di partire e indica il percorso giusto.
        PurgeExecutionMode mode;

        if (!once)
        {
            if (PurgeExecutionModeParser.Parse(args) == PurgeExecutionMode.Delete)
            {
                Console.Error.WriteLine(
                    "La modalita' servizio non puo' cancellare: e' dry-run per costruzione. " +
                    "Per un'esecuzione reale usare 'purge once --delete', pianificata da UC4.");
                return 2;
            }

            mode = PurgeExecutionMode.DryRun;
        }
        else
        {
            var richiesta = PurgeExecutionModeParser.Parse(args);
            if (richiesta is null)
            {
                Console.Error.WriteLine(PurgeExecutionModeParser.Usage);
                return 2;
            }

            mode = richiesta.Value;
        }

        using var host = BuildHost(args, once);

        // La configurazione non decide piu' la modalita': la sovrascrive la
        // riga di comando. Purge:DryRun in appsettings resta solo come default
        // di sviluppo e in produzione non ha effetto.
        var opzioni = host.Services.GetRequiredService<IOptions<PurgeOptions>>().Value;
        opzioni.DryRun = mode == PurgeExecutionMode.DryRun;

        var gate = host.Services.GetRequiredService<PurgeApprovalGate>();
        if (!await gate.IsAllowedAsync(mode, CancellationToken.None).ConfigureAwait(false))
        {
            // Codice diverso da zero, mai uno skip silenzioso: su UC4 un job
            // che esce con zero senza aver fatto niente sembra riuscito.
            Console.Error.WriteLine(
                "Esecuzione in modalita' DELETE rifiutata: policy non approvata.");
            return 3;
        }
        if (!once)
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }

        return await RunOnceAsync(host, args).ConfigureAwait(false);
    }

    /// <summary>
    /// Approva la policy sotto cui e' girato un dry-run concluso.
    ///
    /// Cio' che si approva e' la policy, non il run: il dry-run e' la prova
    /// che qualcuno ha esaminato un report prodotto con quella
    /// configurazione. Se la policy cambia, l'impronta cambia e l'approvazione
    /// non vale piu'.
    ///
    /// Comando separato dall'esecuzione di proposito: un'approvazione che
    /// potesse essere data dallo stesso processo che poi cancella non sarebbe
    /// un controllo.
    /// </summary>
    private static async Task<int> ApproveAsync(IHost host, string[] args)
    {
        var store = host.Services.GetRequiredService<PurgeRunStore>();
        var options = host.Services.GetRequiredService<IOptions<PurgeOptions>>().Value;

        if (args.Length < 2 || !Guid.TryParse(args[1], out var runId))
        {
            Console.Error.WriteLine("Uso: purge approve <dry-run-id> --by <nome> [--note <testo>]");
            return 2;
        }

        var by = ValoreOpzione(args, "--by");
        if (string.IsNullOrWhiteSpace(by))
        {
            Console.Error.WriteLine(
                "--by e' obbligatorio: un'approvazione senza un nome non e' un'approvazione.");
            return 2;
        }

        var run = await store.ReadPolicyAsync(runId, CancellationToken.None).ConfigureAwait(false);

        if (run is null)
        {
            Console.Error.WriteLine($"Run {runId} inesistente su questo database.");
            return 2;
        }

        if (!run.Value.DryRun)
        {
            Console.Error.WriteLine(
                $"Run {runId} non e' un dry-run: si approva un report esaminato, " +
                "non una cancellazione gia' avvenuta.");
            return 2;
        }

        if (run.Value.Phase != RunPhase.Completed)
        {
            Console.Error.WriteLine(
                $"Run {runId} e' in stato {run.Value.Phase}: un dry-run non concluso " +
                "non ha prodotto un report completo.");
            return 2;
        }

        var hashCorrente = PurgePolicy.ComputeHash(options);

        if (run.Value.PolicyHash != hashCorrente)
        {
            Console.Error.WriteLine(
                $"Il dry-run {runId} e' girato con una policy diversa da quella configurata " +
                "adesso. Approvarlo autorizzerebbe una cancellazione che nessuno ha esaminato.");
            Console.Error.WriteLine($"  dry-run:  {run.Value.PolicyHash}");
            Console.Error.WriteLine($"  corrente: {hashCorrente}");
            return 2;
        }

        await store.ApproveAsync(hashCorrente, runId, by!, PurgePolicy.Describe(options),
            ValoreOpzione(args, "--note"), CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"Policy approvata da {by}.");
        Console.WriteLine($"  {PurgePolicy.Describe(options)}");
        Console.WriteLine($"  impronta {hashCorrente}");
        Console.WriteLine($"  dry-run di riferimento {runId}");
        return 0;
    }

    /// <summary>
    /// Allinea lo schema Purge agli script incorporati (D-13). Comando
    /// separato dall'esecuzione: il purge notturno non deve modificare lo
    /// schema, e chi lo lancia deve avere permessi DDL che l'utenza del
    /// purge non ha. Con --status non applica niente.
    ///
    /// Non e' un sostituto di 000_install_purge.sql per chi installa con
    /// sqlcmd: e' l'equivalente tracciato, per chi preferisce che sia il
    /// programma a dire cosa manca.
    /// </summary>
    private static int Migrate(IHost host, string[] args)
    {
        var migrator = host.Services.GetRequiredService<SchemaMigrator>();
        var soloStato = args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase));

        try
        {
            var stato = migrator.Status();

            Console.WriteLine($"Schema Purge: {stato.Applied.Count} script applicati, {stato.Pending.Count} da applicare.");
            foreach (var s in stato.Pending) Console.WriteLine($"  da applicare: {s}");

            if (soloStato) return stato.Pending.Count == 0 ? 0 : 5;

            var applicati = migrator.Migrate();
            foreach (var s in applicati) Console.WriteLine($"  applicato:    {s}");
            Console.WriteLine(applicati.Count == 0 ? "Schema gia' allineato." : "Schema allineato.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Migrazione fallita: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Rinuncia al limite di fine della finestra operativa. Esplicito di
    /// proposito: serve a un recupero o a un collaudo, non alla notte normale.
    /// </summary>
    public const string NoWindowFlag = "--no-window";

    private static string? ValoreOpzione(string[] args, string nome)
    {
        var i = Array.FindIndex(args, a => a.Equals(nome, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IHost BuildHost(string[] args, bool once)
    {
        // Content root ancorato alla cartella dell'eseguibile e non alla
        // directory di lavoro: altrimenti appsettings.json non viene trovato
        // quando il processo e' avviato da un percorso diverso — tipicamente
        // da Visual Studio, da uno scheduler o da un servizio Windows.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

        Configure(builder, once);
        return builder.Build();
    }

    private static void Configure(HostApplicationBuilder builder, bool once)
    {
        // optional: false — un file di configurazione assente deve fallire
        // subito e per nome, non produrre silenziosamente una configurazione
        // vuota che si manifesta come NullReference molto piu' a valle.
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        builder.Configuration
            .AddJsonFile(settingsPath, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables("PURGE_");

        builder.Services
            .AddOptions<PurgeOptions>()
            .Bind(builder.Configuration.GetSection(PurgeOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.WindowStart != o.WindowEnd || !o.WindowEnabled,
                      "Finestra operativa di durata nulla.")
            .Validate(o => o.Strategies.Count > 0, "Nessuna strategia configurata.")
            .Validate(o => o.HousekeepingWindowsAreConsistent,
                      "FailedStagingRetentionDays deve essere >= StagingRetentionDays.")
            .Validate(o => o.WindowGrace >= TimeSpan.Zero,
                      "WindowGrace non puo' essere negativa.")
            .ValidateOnStart();

        var cs = builder.Configuration.GetConnectionString("PaymentOrder");
        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PaymentOrder non configurata. " +
                $"File letto: {settingsPath} (esiste: {File.Exists(settingsPath)}). " +
                "In alternativa impostare la variabile d'ambiente " +
                "PURGE_ConnectionStrings__PaymentOrder.");
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<PurgeMetrics>();

        builder.Services.AddSingleton(sp => new SqlExecutor(
            cs, sp.GetRequiredService<IOptions<PurgeOptions>>().Value.CommandTimeoutSeconds));

        // Registrata anche sotto ISqlExecutor, e con Resolve invece di una
        // seconda istanza: PurgeExecutionLock e SliceExecutor usano ancora il
        // tipo concreto, e due SqlExecutor distinti sarebbero due configurazioni
        // che possono divergere.
        builder.Services.AddSingleton<ISqlExecutor>(
            sp => sp.GetRequiredService<SqlExecutor>());

        builder.Services.AddSingleton<SchemaVerifier>();
        builder.Services.AddSingleton(sp => new SchemaMigrator(
            cs,
            sp.GetRequiredService<ILogger<SchemaMigrator>>(),
            sp.GetRequiredService<IOptions<PurgeOptions>>().Value.CommandTimeoutSeconds));
        builder.Services.AddSingleton<PurgeHousekeeping>();
        builder.Services.AddSingleton<PurgeRunStore>();
        builder.Services.AddSingleton<BatchedStatementRunner>();
        builder.Services.AddSingleton<PurgeApprovalGate>();
        builder.Services.AddSingleton<IPurgeStrategy, TerminatedStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, AbandonedStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, StandingOrdersStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, CollectiveStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, OrphanHistoryStrategy>();
        builder.Services.AddSingleton<PurgeStrategyResolver>();
        builder.Services.AddSingleton<PreDeleteValidator>();

        // Registrazione esplicita: il timeout del bulk copy deve seguire
        // CommandTimeoutSeconds. Con AddSingleton<BatchPlanner>() prendeva il
        // valore di default del costruttore, che coincideva con quello
        // configurato solo finche' nessuno toccava appsettings.
        builder.Services.AddSingleton(sp => new BatchPlanner(
            sp.GetRequiredService<SqlExecutor>(),
            sp.GetRequiredService<PurgeStrategyResolver>(),
            sp.GetRequiredService<ILogger<BatchPlanner>>(),
            bulkCopyTimeoutSeconds: sp.GetRequiredService<IOptions<PurgeOptions>>()
                                      .Value.CommandTimeoutSeconds));

        builder.Services.AddSingleton<DryRunReporter>();
        builder.Services.AddSingleton<SliceExecutor>();
        builder.Services.AddSingleton<IBatchWorkProvider, PurgeRunBatchWorkProvider>();
        builder.Services.AddSingleton<IBatchExecutor, SliceBatchExecutor>();
        builder.Services.AddSingleton<IBatchExecutionCoordinator, BatchExecutionCoordinator>();
        builder.Services.AddSingleton<IPurgePhase, SelectingPhase>();
        builder.Services.AddSingleton<IPurgePhase, ExpandingPhase>();
        builder.Services.AddSingleton<IPurgePhase, ValidatingPhase>();
        builder.Services.AddSingleton<IPurgePhase, PlanningPhase>();
        builder.Services.AddSingleton<IPurgePhase, ExecutingPhase>();
        builder.Services.AddSingleton<PurgeWindowGuard>();
        builder.Services.AddSingleton<PurgeExecutionLock>();
        builder.Services.AddSingleton<RetentionOrchestrator>();

        if (!once) builder.Services.AddHostedService<RetentionCronService>();
    }

    private static async Task<int> RunOnceAsync(IHost host, string[] args)
    {
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RunOnce");
        var store = host.Services.GetRequiredService<PurgeRunStore>();
        var orchestrator = host.Services.GetRequiredService<RetentionOrchestrator>();
        var executionLock = host.Services.GetRequiredService<PurgeExecutionLock>();
        var options = host.Services.GetRequiredService<IOptions<PurgeOptions>>().Value;
        var clock = host.Services.GetRequiredService<TimeProvider>();

        // La finestra operativa resta attiva anche in modalita' once.
        //
        // Qui c'era una disattivazione incondizionata, motivata dal fatto che
        // e' lo scheduler esterno a decidere quando eseguire. La motivazione
        // copriva pero' meta' del problema: la finestra decide *quando
        // partire*, e questo lo fa UC4, ma decide anche *quando fermarsi*, e
        // quello non lo fa nessun altro.
        //
        // Con la disattivazione, un run piu' lungo del previsto proseguiva
        // nella mattina lavorativa in concorrenza con l'operativita'. Il
        // pacing fra le slice, la priorita' bassa sui deadlock e le
        // transazioni corte servono a convivere con l'OLTP *dentro* una
        // finestra notturna: fuori riducono il danno, non lo evitano.
        //
        // Rinunciare al limite di fine deve restare possibile — un recupero,
        // un collaudo — ma come richiesta esplicita, non come effetto
        // collaterale della modalita' di avvio.
        var senzaFinestra = args.Any(
            a => a.Equals(NoWindowFlag, StringComparison.OrdinalIgnoreCase));

        if (senzaFinestra)
        {
            options.WindowEnabled = false;
            log.LogWarning(
                "PurgeWindowDisabled: {Flag} richiesto esplicitamente. L'esecuzione " +
                "non si fermera' alla chiusura della finestra operativa.", NoWindowFlag);
        }
        else if (options.WindowEnabled)
        {
            log.LogInformation(
                "Finestra operativa attiva: {Start}-{End}. Nessuna nuova slice verra' " +
                "avviata dopo la chiusura.", options.WindowStart, options.WindowEnd);
        }

        // Riattivare la finestra apre un caso che prima non esisteva: UC4
        // pianificato a un'ora fuori dalla finestra configurata. Il
        // coordinatore si fermerebbe alla prima verifica e il processo
        // uscirebbe con zero senza aver fatto niente — un job verde che non
        // ha cancellato nulla, che e' il modo peggiore di sbagliare.
        //
        // Le due configurazioni sono in disaccordo e qualcuno deve saperlo.
        if (options.WindowEnabled && !options.IsWithinWindow(clock.GetLocalNow()))
        {
            log.LogError(
                "PurgeOutsideWindow: avvio alle {Now:HH:mm} fuori dalla finestra " +
                "{Start}-{End}. La pianificazione dello scheduler e la finestra " +
                "configurata non concordano: allinearle, oppure usare {Flag} se " +
                "l'esecuzione fuori orario e' voluta.",
                clock.GetLocalNow(), options.WindowStart, options.WindowEnd, NoWindowFlag);

            return 4;
        }

        var strategies = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                         && Enum.TryParse<RetentionStrategy>(args[1], true, out var s)
            ? new List<RetentionStrategy> { s }
            : options.Strategies;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var lease = await executionLock.TryAcquireAsync(cts.Token).ConfigureAwait(false);
        if (lease is null)
        {
            log.LogWarning("Un'altra istanza del purge è già in esecuzione.");
            return 0;
        }

        // Una divergenza fra topologia e database va segnalata prima di creare

        // qualsiasi run, non a meta' della fase di validazione.

        try

        {

            await host.Services.GetRequiredService<SchemaVerifier>()

                      .EnsureAsync(cts.Token).ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            log.LogError(ex, "Verifica dello schema fallita: esecuzione interrotta.");

            return 2;

        }

        // La finestra vale anche per le fasi che non la controllano da se'.
        // Il coordinatore continua a fermarsi in modo ordinato a fine finestra;
        // questo token interviene solo se una fase la supera oltre la tolleranza.
        using var window = host.Services.GetRequiredService<PurgeWindowGuard>()
                               .Open(cts.Token);

        var failed = false;
        var sforata = false;

        foreach (var strategy in strategies)
        {
            try
            {
                var runId = await store.FindResumableAsync(strategy, window.Token).ConfigureAwait(false)
                            ?? await store.CreateAsync(strategy, options, clock.GetLocalNow(), window.Token)
                                          .ConfigureAwait(false);

                Console.WriteLine($"{strategy,-16} run {runId}  DryRun={options.DryRun}");
                await orchestrator.RunAsync(runId, window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!window.ClosedByWindow)
            {
                log.LogWarning("Interrotto dall'operatore.");
                return 130;
            }
            catch (OperationCanceledException)
            {
                // Lo sforamento e' gia' stato registrato dal guardiano. Qui si
                // decide solo che le strategie rimaste non partono: aprirne una
                // nuova fuori finestra sarebbe la cosa che stiamo evitando.
                log.LogError(
                    "Strategia {Strategy} interrotta dalla chiusura della finestra: " +
                    "le strategie successive sono rinviate.", strategy);

                sforata = true;
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Strategia {Strategy} fallita.", strategy);
                failed = true;
            }
        }

        // Lo staging dei run conclusi non serve piu': va sfoltito a sua volta.

        try

        {

            await host.Services.GetRequiredService<PurgeHousekeeping>()

                      .RunAsync(cts.Token).ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            log.LogError(ex, "Housekeeping fallito.");

        }

        // Codice dedicato allo sforamento: non e' un fallimento dei dati — il run
        // riprende dal checkpoint la notte successiva — ma non e' nemmeno una
        // notte riuscita, e uscire con zero lo renderebbe invisibile a UC4.
        if (sforata) return 5;

        return failed ? 1 : 0;
    }
}
