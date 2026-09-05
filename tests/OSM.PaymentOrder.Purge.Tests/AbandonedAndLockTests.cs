using Microsoft.Extensions.DependencyInjection;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Gli ultimi due percorsi funzionali senza copertura.
///
/// La strategia Abandoned e' l'unica delle cinque mai esercitata, e ha due
/// particolarita' che nessun altro test tocca: ancoraggio su CreationDate
/// anziche' su ExecutionDate, e insieme di stati eleggibili diverso. Se
/// CutoffOf o EligibleStates fossero sbagliati per questa strategia, oggi
/// nessuno se ne accorgerebbe.
///
/// PurgeExecutionLock impedisce due esecuzioni concorrenti: se non funzionasse,
/// due run sulle stesse tabelle produrrebbero deadlock o doppie cancellazioni.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class AbandonedAndLockTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    private static PurgeOptions WithAbandoned(PurgeOptions source, int months) => new()
    {
        RetentionYears = source.RetentionYears,
        AnchorMode = source.AnchorMode,
        AbandonedRetentionMonths = months,
        AbandonedEnabled = true,
        MaxRowsPerBatch = source.MaxRowsPerBatch,
        MaxOrdersPerBatch = source.MaxOrdersPerBatch,
        MaxSliceAttempts = source.MaxSliceAttempts,
        InterSliceDelay = source.InterSliceDelay,
        RetryDelay = source.RetryDelay,
        CommandTimeoutSeconds = source.CommandTimeoutSeconds,
        WindowEnabled = false,
        DryRun = false,
        HousekeepingEnabled = false,
        StagingRetentionDays = source.StagingRetentionDays,
        FailedStagingRetentionDays = source.FailedStagingRetentionDays,
        HousekeepingBatchSize = source.HousekeepingBatchSize
    };

    private async Task<PurgeRun> RunAbandonedAsync(int months = 24)
    {
        var options = WithAbandoned(db.Options, months);
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Abandoned, options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);
        return await db.Store.LoadAsync(runId, default);
    }

    // ==================================================================
    // Strategia Abandoned
    // ==================================================================

    /// <summary>
    /// Un ordine mai autorizzato e vecchio va sfoltito; uno recente no.
    ///
    /// L'ancoraggio e' CreationDate, non ExecutionDate: un ordine in Created
    /// non ha prodotto alcuna scrittura contabile, quindi la data di esecuzione
    /// non ha significato. Il test lo verifica dando ai due ordini la stessa
    /// ExecutionDate remota e CreationDate diverse: se il motore usasse
    /// l'ancoraggio sbagliato, li cancellerebbe entrambi.
    /// </summary>
    [Fact]
    public async Task Abbandonato_vecchio_cancellato_recente_conservato()
    {
        var remota = DateTime.Today.AddYears(-10);

        var vecchio = await Seed.AddOrderAsync(
            statusCode: "Created",
            executionDate: remota,
            creationDate: DateTime.Today.AddMonths(-36),
            revisions: 1);

        var recente = await Seed.AddOrderAsync(
            statusCode: "Created",
            executionDate: remota,               // stessa data di esecuzione
            creationDate: DateTime.Today.AddMonths(-3),
            revisions: 1);

        var run = await RunAbandonedAsync(months: 24);

        Assert.Equal(RunPhase.Completed, run.Phase);
        Assert.Equal(1, await Seed.CountAsync("[Order]"));

        var superstite = await db.Sql.ScalarAsync<Guid>(
            "SELECT TOP (1) Id FROM PaymentOrder.[Order];", default);

        Assert.Equal(recente, superstite);
        Assert.NotEqual(vecchio, superstite);
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());
    }

    /// <summary>
    /// PartiallyAuthorised e' eleggibile: e' una firma su due, non un'operazione
    /// in corso. Ma la soglia deve essere ampia, perche' l'ordine potrebbe
    /// essere in attesa del secondo firmatario (PA-21).
    /// </summary>
    [Fact]
    public async Task PartiallyAuthorised_vecchio_e_eleggibile()
    {
        await Seed.AddOrderAsync(
            statusCode: "PartiallyAuthorised",
            creationDate: DateTime.Today.AddMonths(-40),
            revisions: 1);

        await RunAbandonedAsync(months: 24);

        Assert.Equal(0, await Seed.CountAsync("[Order]"));
    }

    /// <summary>
    /// Gli stati attivi non sono candidati, per quanto vecchi.
    ///
    /// E' l'invariante piu' importante di questa strategia: un ordine vecchio
    /// in Processing o Transmitted e' un'anomalia applicativa da investigare,
    /// non un abbandono. Cancellarlo nasconderebbe il problema anziche'
    /// risolverlo, e potrebbe riguardare un pagamento realmente in corso.
    /// </summary>
    [Theory]
    [InlineData("TotallyAuthorised")]
    [InlineData("Transmitted")]
    [InlineData("ToBeProcessed")]
    [InlineData("Processing")]
    [InlineData("Suspended")]
    [InlineData("Cancelling")]
    [InlineData("Deleting")]
    public async Task Stati_attivi_non_sono_mai_abbandonati(string statusCode)
    {
        await Seed.AddOrderAsync(
            statusCode: statusCode,
            creationDate: DateTime.Today.AddYears(-15),   // vecchissimo
            revisions: 1);

        await RunAbandonedAsync(months: 1);               // soglia minima

        Assert.Equal(1, await Seed.CountAsync("[Order]"));
    }

    /// <summary>
    /// Un ordine in stato Model non e' un abbandono: e' un template, e non
    /// deve essere sfoltito da questa strategia (caso C9).
    /// </summary>
    [Fact]
    public async Task Ordine_in_stato_Model_non_e_un_abbandono()
    {
        await Seed.AddOrderAsync(
            statusCode: "Model",
            creationDate: DateTime.Today.AddYears(-15));

        await RunAbandonedAsync(months: 1);

        Assert.Equal(1, await Seed.CountAsync("[Order]"));
    }

    /// <summary>
    /// La strategia Abandoned usa AbandonedCutoff, non RetentionCutoff:
    /// sono due soglie distinte e congelate separatamente sul run.
    /// </summary>
    [Fact]
    public async Task Abandoned_usa_la_soglia_dedicata()
    {
        var options = WithAbandoned(db.Options, months: 24);
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Abandoned, options, DateTimeOffset.Now, default);

        var run = await db.Store.LoadAsync(runId, default);
        var strategy = db.Services.GetRequiredService<PurgeStrategyResolver>()
                                  .Resolve(RetentionStrategy.Abandoned);

        Assert.NotNull(run.AbandonedCutoff);
        Assert.Equal(run.AbandonedCutoff!.Value, strategy.CutoffOf(run));
        Assert.NotEqual(run.RetentionCutoff, strategy.CutoffOf(run));
    }

    // ==================================================================
    // Lock di esecuzione
    // ==================================================================

    /// <summary>
    /// Due acquisizioni concorrenti: la seconda deve fallire.
    ///
    /// Senza il lock, due run sulle stesse tabelle di staging produrrebbero
    /// deadlock o cancellazioni doppie. sp_getapplock con LockOwner = Session
    /// si rilascia da solo alla caduta della connessione, quindi un processo
    /// ucciso non lascia un lock orfano.
    /// </summary>
    [Fact]
    public async Task Il_lock_impedisce_due_esecuzioni_concorrenti()
    {
        var primo = db.Services.GetRequiredService<PurgeExecutionLock>();
        var secondo = new PurgeExecutionLock(db.Sql);

        await using var handle = await primo.TryAcquireAsync(default);
        Assert.NotNull(handle);

        var negato = await secondo.TryAcquireAsync(default);
        Assert.Null(negato);
    }

    /// <summary>Rilasciato il primo, il secondo deve poterlo acquisire.</summary>
    [Fact]
    public async Task Il_lock_viene_rilasciato_e_riacquisito()
    {
        var gestore = db.Services.GetRequiredService<PurgeExecutionLock>();

        var primo = await gestore.TryAcquireAsync(default);
        Assert.NotNull(primo);
        await primo!.DisposeAsync();

        var secondo = await new PurgeExecutionLock(db.Sql).TryAcquireAsync(default);
        Assert.NotNull(secondo);
        await secondo!.DisposeAsync();
    }
}
