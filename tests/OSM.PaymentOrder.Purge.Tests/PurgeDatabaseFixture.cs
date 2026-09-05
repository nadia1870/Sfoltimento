using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Engine.Phases;
using OSM.PaymentOrder.Purge.Observability;

namespace OSM.PaymentOrder.Purge.Tests;
/// <summary>
/// Crea un database dedicato per l'intera sessione di test e lo distrugge alla
/// fine. Non tocca alcun database esistente.
/// </summary>
public sealed class PurgeDatabaseFixture : IAsyncLifetime
{
    private static readonly string MasterConnection =
        Environment.GetEnvironmentVariable("PURGE_TEST_SQL")
        ?? @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true";

    public string DatabaseName { get; } = $"PurgeTests_{Guid.NewGuid():N}"[..24];
    public string ConnectionString { get; private set; } = string.Empty;
    public ServiceProvider Services { get; private set; } = null!;
    public SqlExecutor Sql { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await ExecuteOnMasterAsync($"CREATE DATABASE [{DatabaseName}];");
        var builder = new SqlConnectionStringBuilder(MasterConnection) { InitialCatalog = DatabaseName };
        ConnectionString = builder.ConnectionString;
        Sql = new SqlExecutor(ConnectionString);

        foreach (var script in new[]
                 { "010_test_schema.sql", "001_purge_schema.sql", "005_housekeeping.sql",
                   "006_collective_atomicity.sql" })
            await RunScriptAsync(script);

        Services = BuildServices();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await ExecuteOnMasterAsync(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}];");
    }

    public PurgeOptions Options => Services.GetRequiredService<IOptions<PurgeOptions>>().Value;
    public RetentionOrchestrator Orchestrator => Services.GetRequiredService<RetentionOrchestrator>();
    public PurgeRunStore Store => Services.GetRequiredService<PurgeRunStore>();

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddSingleton<PurgeMetrics>();
        services.AddSingleton(new SqlExecutor(ConnectionString));

        services.AddOptions<PurgeOptions>().Configure(o =>
        {
            o.DryRun = false;
            o.WindowEnabled = false;
            o.HousekeepingEnabled = false;
            o.MaxRowsPerBatch = 50;
            o.MaxOrdersPerBatch = 10;
            o.InterSliceDelay = TimeSpan.Zero;
            o.RetryDelay = TimeSpan.Zero;
        });

        services.AddSingleton<SchemaVerifier>();
        services.AddSingleton<PurgeHousekeeping>();
        services.AddSingleton<PurgeRunStore>();

        services.AddSingleton<IPurgeStrategy, TerminatedStrategy>();
        services.AddSingleton<IPurgeStrategy, AbandonedStrategy>();
        services.AddSingleton<IPurgeStrategy, StandingOrdersStrategy>();
        services.AddSingleton<IPurgeStrategy, CollectiveStrategy>();
        services.AddSingleton<IPurgeStrategy, OrphanHistoryStrategy>();
        services.AddSingleton<PurgeStrategyResolver>();
        services.AddSingleton<IPurgeStrategyResolver>(sp => sp.GetRequiredService<PurgeStrategyResolver>());

        services.AddSingleton<PreDeleteValidator>();
        services.AddSingleton<BatchPlanner>();
        services.AddSingleton<DryRunReporter>();

        services.AddSingleton<SliceExecutor>();

        // V5: ExecutingPhase delega l'esecuzione delle slice al coordinator.
        services.AddSingleton<BatchExecutionCoordinator>();
        services.AddSingleton<IBatchExecutionCoordinator>(sp => sp.GetRequiredService<BatchExecutionCoordinator>());

        services.AddSingleton<CollectiveTailExecutor>();
        services.AddSingleton<IPurgePhase, SelectingPhase>();
        services.AddSingleton<IPurgePhase, ExpandingPhase>();
        services.AddSingleton<IPurgePhase, ValidatingPhase>();
        services.AddSingleton<IPurgePhase, PlanningPhase>();
        services.AddSingleton<IPurgePhase, ExecutingPhase>();
        services.AddSingleton<IPurgePhase, CollectiveTailPhase>();

        services.AddSingleton<PurgeExecutionLock>();
        services.AddSingleton<RetentionOrchestrator>();

        return services.BuildServiceProvider();
    }

    private async Task RunScriptAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var text = await File.ReadAllTextAsync(path);
        var batches = Regex.Split(text, @"^\s*GO\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task ResetAsync()
    {
        string[] paymentOrderTables =
        [
            "AccountTransferHistory", "BankTransferHistory",
            "BankTransferToCornerCardHistory", "ForeignBankTransferHistory",
            "InpaymentSlipHistory", "IpBankTransferHistory", "IpQRBillHistory",
            "QRBillHistory", "RealTimeCardReloadHistory", "StandingOrderHistory",
            "CollectiveOrderGroupOrderHistory", "OrderHistory",
            "CollectiveOrderGroupHistory", "CollectiveOrderGroupOrder",
            "CollectiveOrderGroup", "CollectiveOrderContent",
            "CollectiveOrderHistory", "CollectiveOrder",
            "AccountTransfer", "BankTransfer", "BankTransferToCornerCard",
            "ForeignBankTransfer", "InpaymentSlip", "IpBankTransfer", "IpQRBill",
            "QRBill", "RealTimeCardReload", "StandingOrder",
            "Model", "Category", "[Order]"
        ];

        foreach (var table in paymentOrderTables)
            await Sql.ExecuteAsync($"DELETE FROM PaymentOrder.{table};", default);

        string[] purgeTables =
        [
            "RunCandidateOrderHistory", "RunCandidateOrder", "RunCandidateCollective",
            "RunBatchProgress", "ValidationFinding", "DryRunReport", "PurgeAudit",
            "PurgeRun"
        ];

        foreach (var table in purgeTables)
            await Sql.ExecuteAsync($"DELETE FROM Purge.{table};", default);
    }

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var conn = new SqlConnection(MasterConnection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("PurgeDatabase")]
public sealed class PurgeDatabaseCollection : ICollectionFixture<PurgeDatabaseFixture>;
