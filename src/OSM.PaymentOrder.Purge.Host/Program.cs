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
using OSM.PaymentOrder.Purge.Observability;

namespace OSM.PaymentOrder.Purge.Host;

public static class Program
{
    /// <summary>
    /// Due modalita':
    ///   dotnet run                  servizio con cronjob interno
    ///   dotnet run -- once [strat]  esecuzione singola, per scheduler esterno
    ///                               (Task Scheduler, cron di sistema, k8s CronJob)
    ///
    /// La modalita' 'once' ignora la finestra oraria: e' lo scheduler esterno
    /// a decidere quando eseguire.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var once = args.Length > 0 &&
                   args[0].Equals("once", StringComparison.OrdinalIgnoreCase);

        using var host = BuildHost(args, once);
        if (!once)
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }

        return await RunOnceAsync(host, args).ConfigureAwait(false);
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

        builder.Services.AddSingleton<SchemaVerifier>();
        builder.Services.AddSingleton<PurgeHousekeeping>();
        builder.Services.AddSingleton<PurgeRunStore>();
        builder.Services.AddSingleton<IPurgeStrategy, TerminatedStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, AbandonedStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, StandingOrdersStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, CollectiveStrategy>();
        builder.Services.AddSingleton<IPurgeStrategy, OrphanHistoryStrategy>();
        builder.Services.AddSingleton<PurgeStrategyResolver>();
builder.Services.AddSingleton<IPurgeStrategyResolver>(sp => sp.GetRequiredService<PurgeStrategyResolver>());
        builder.Services.AddSingleton<PreDeleteValidator>();
        builder.Services.AddSingleton<BatchPlanner>();
        builder.Services.AddSingleton<DryRunReporter>();
        builder.Services.AddSingleton<SliceExecutor>();
        builder.Services.AddSingleton<BatchExecutionCoordinator>();
        builder.Services.AddSingleton<IBatchExecutionCoordinator>(sp => sp.GetRequiredService<BatchExecutionCoordinator>());
        builder.Services.AddSingleton<CollectiveTailExecutor>();
        builder.Services.AddSingleton<IPurgePhase, SelectingPhase>();
        builder.Services.AddSingleton<IPurgePhase, ExpandingPhase>();
        builder.Services.AddSingleton<IPurgePhase, ValidatingPhase>();
        builder.Services.AddSingleton<IPurgePhase, PlanningPhase>();
        builder.Services.AddSingleton<IPurgePhase, ExecutingPhase>();
        builder.Services.AddSingleton<IPurgePhase, CollectiveTailPhase>();
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

        // Lo scheduler esterno decide quando eseguire: la finestra non si applica.
        options.WindowEnabled = false;

        var strategies = args.Length > 1 && Enum.TryParse<RetentionStrategy>(args[1], true, out var s)
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

        var failed = false;
        foreach (var strategy in strategies)
        {
            try
            {
                var runId = await store.FindResumableAsync(strategy, cts.Token).ConfigureAwait(false)
                            ?? await store.CreateAsync(strategy, options, clock.GetLocalNow(), cts.Token)
                                          .ConfigureAwait(false);

                Console.WriteLine($"{strategy,-16} run {runId}  DryRun={options.DryRun}");
                await orchestrator.RunAsync(runId, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("Interrotto dall'operatore.");
                return 130;
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

        return failed ? 1 : 0;
    }
}
