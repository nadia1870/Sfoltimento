using Microsoft.Extensions.DependencyInjection;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.Phases;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Percorsi di errore e di resilienza.
///
/// I test in RetentionInvariantsTests verificano che il motore faccia la cosa
/// giusta quando tutto va bene. Questi verificano che si comporti correttamente
/// quando qualcosa va storto — che e' poi la ragione per cui ha quella
/// struttura: slice atomiche, checkpoint, retry, validazioni.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class RetentionResilienceTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    // ------------------------------------------------------------------
    // Utilita': esecuzione delle fasi una per una, per poter intervenire
    // sui dati fra una e l'altra. L'orchestratore le esegue tutte insieme,
    // quindi non consentirebbe di simulare una modifica concorrente.
    // ------------------------------------------------------------------

    private IPurgePhase PhaseFor(RunPhase phase) =>
        db.Services.GetServices<IPurgePhase>().Single(p => p.Phase == phase);

    private async Task<PurgeRun> RunUpToPlanningAsync(
        RetentionStrategy strategy, PurgeOptions? options = null)
    {
        var runId = await db.Store.CreateAsync(
            strategy, options ?? db.Options, DateTimeOffset.Now, default);

        var run = await db.Store.LoadAsync(runId, default);

        foreach (var phase in new[]
                 {
                     RunPhase.Selecting, RunPhase.Expanding,
                     RunPhase.Validating, RunPhase.Planning
                 })
        {
            var result = await PhaseFor(phase).ExecuteAsync(run, default);

            if (result.NextPhase is { } next)
            {
                await db.Store.SetPhaseAsync(run.RunId, next, default, result.Error);
                run.Phase = next;
            }

            if (result.Stop) break;
        }

        return run;
    }

    private static PurgeOptions CloneOptions(PurgeOptions source, Action<PurgeOptions> mutate)
    {
        var copy = new PurgeOptions
        {
            RetentionYears = source.RetentionYears,
            AnchorMode = source.AnchorMode,
            AbandonedRetentionMonths = source.AbandonedRetentionMonths,
            AbandonedEnabled = source.AbandonedEnabled,
            MaxRowsPerBatch = source.MaxRowsPerBatch,
            MaxOrdersPerBatch = source.MaxOrdersPerBatch,
            MaxSliceAttempts = source.MaxSliceAttempts,
            InterSliceDelay = source.InterSliceDelay,
            RetryDelay = source.RetryDelay,
            CommandTimeoutSeconds = source.CommandTimeoutSeconds,
            WindowEnabled = source.WindowEnabled,
            DryRun = source.DryRun,
            HousekeepingEnabled = source.HousekeepingEnabled,
            StagingRetentionDays = source.StagingRetentionDays,
            FailedStagingRetentionDays = source.FailedStagingRetentionDays,
            HousekeepingBatchSize = source.HousekeepingBatchSize
        };
        mutate(copy);
        return copy;
    }

    // ==================================================================
    // 1. LA SOGLIA
    // ==================================================================

    /// <summary>
    /// Un ordine sotto soglia non deve essere selezionato.
    ///
    /// Tutti i test esistenti creano ordini vecchi dieci anni: se la formula
    /// del cutoff fosse invertita passerebbero ugualmente. E' il buco di
    /// copertura che tocca la correttezza di ogni cancellazione.
    /// </summary>
    [Fact]
    public async Task Ordine_sotto_soglia_non_viene_cancellato()
    {
        var vecchio = await Seed.AddOrderAsync(executionDate: DateTime.Today.AddYears(-10));
        var recente = await Seed.AddOrderAsync(executionDate: DateTime.Today.AddMonths(-3));

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);
        await db.Orchestrator.RunAsync(runId, default);

        Assert.Equal(1, await Seed.CountAsync("[Order]"));

        var superstite = await db.Sql.ScalarAsync<Guid>(
            "SELECT TOP (1) Id FROM PaymentOrder.[Order];", default);
        Assert.Equal(recente, superstite);
        Assert.NotEqual(vecchio, superstite);
    }

    /// <summary>
    /// Le due letture della soglia non sono equivalenti: per un'operazione di
    /// inizio anno lo scarto arriva a dodici mesi (PA-3). Il test fissa il
    /// comportamento atteso di entrambe, cosi' un cambio accidentale del
    /// default si manifesta subito.
    /// </summary>
    [Fact]
    public void Le_due_letture_della_soglia_differiscono_come_atteso()
    {
        var riferimento = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

        var mobile = CloneOptions(db.Options, o =>
        {
            o.AnchorMode = RetentionAnchorMode.RollingDate;
            o.RetentionYears = 5;
        }).ComputeRetentionCutoff(riferimento);

        var esercizio = CloneOptions(db.Options, o =>
        {
            o.AnchorMode = RetentionAnchorMode.FiscalYearEnd;
            o.RetentionYears = 5;
        }).ComputeRetentionCutoff(riferimento);

        Assert.Equal(new DateTime(2021, 6, 15), mobile);
        Assert.Equal(new DateTime(2021, 1, 1), esercizio);

        // La lettura per esercizio e' la piu' prudente: conserva di piu'.
        Assert.True(esercizio < mobile);
    }

    // ==================================================================
    // 2. IL ROLLBACK
    // ==================================================================

    /// <summary>
    /// Se un ordine cambia stato fra selezione ed esecuzione, la slice non deve
    /// cancellare NULLA — non una parte.
    ///
    /// E' l'invariante che giustifica l'intero disegno a slice atomiche:
    /// procedere lascerebbe a database un ordine privo di storico e dettagli,
    /// che e' molto peggio del non fare nulla.
    /// </summary>
    [Fact]
    public async Task Cambio_di_stato_in_corsa_non_lascia_cancellazioni_parziali()
    {
        var ordini = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ordini.Add(await Seed.AddOrderAsync(revisions: 2));

        // Fasi fino al planning: i candidati sono congelati.
        var run = await RunUpToPlanningAsync(RetentionStrategy.Terminated);

        // Un ordine torna in lavorazione dopo la selezione.
        await Seed.SetStatusAsync(ordini[1], "Processing");

        var executor = db.Services.GetRequiredService<SliceExecutor>();
        var slice = await db.Store.NextPendingSliceAsync(run.RunId, default);
        Assert.NotNull(slice);

        var result = await executor.ExecuteAsync(run, slice!, default);

        Assert.Equal(SliceOutcome.Retryable, result.Outcome);

        // Rollback integrale: nessuna riga cancellata, in nessuna tabella.
        Assert.Equal(3, await Seed.CountAsync("[Order]"));
        Assert.Equal(6, await Seed.CountAsync("OrderHistory"));
        Assert.Equal(3, await Seed.CountAsync("BankTransfer"));
        Assert.Equal(6, await Seed.CountAsync("BankTransferHistory"));
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());
    }

    /// <summary>
    /// Dopo MaxSliceAttempts la slice viene abbandonata e il run prosegue:
    /// un singolo aggregato problematico non deve bloccare lo sfoltimento.
    /// </summary>
    [Fact]
    public async Task Slice_ripetutamente_fallita_viene_abbandonata()
    {
        var bloccante = await Seed.AddOrderAsync(revisions: 1);

        var run = await RunUpToPlanningAsync(RetentionStrategy.Terminated);
        await Seed.SetStatusAsync(bloccante, "Processing");

        var executor = db.Services.GetRequiredService<SliceExecutor>();
        var slice = await db.Store.NextPendingSliceAsync(run.RunId, default);

        for (var attempt = 0; attempt < db.Options.MaxSliceAttempts; attempt++)
        {
            var result = await executor.ExecuteAsync(run, slice!, default);
            Assert.Equal(SliceOutcome.Retryable, result.Outcome);
            await db.Store.RecordAttemptAsync(run.RunId, slice!.BatchNo, result.Reason, default);
        }

        await db.Store.AbandonSliceAsync(run.RunId, slice!.BatchNo, "test", default);

        var abbandonate = await db.Sql.ScalarAsync<long>("""
            SELECT COUNT_BIG(*) FROM Purge.RunBatchProgress
            WHERE RunId = @RunId AND Status = 'Abandoned';
            """, default, SqlParam.Of("@RunId", run.RunId));

        Assert.Equal(1, abbandonate);
        Assert.Equal(1, await Seed.CountAsync("[Order]"));   // nulla cancellato
    }

    // ==================================================================
    // 3. IL DRY-RUN
    // ==================================================================

    /// <summary>
    /// Il dry-run non deve cancellare nulla e deve comunque produrre il report.
    /// E' la modalita' con cui il motore girera' per prima in produzione, e ha
    /// gia' prodotto un difetto: il report assente a zero candidati.
    /// </summary>
    [Fact]
    public async Task Dry_run_non_cancella_e_produce_il_report()
    {
        for (var i = 0; i < 5; i++) await Seed.AddOrderAsync(revisions: 2);

        var options = CloneOptions(db.Options, o => o.DryRun = true);
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);
        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Completed, run.Phase);

        // Dati intatti.
        Assert.Equal(5, await Seed.CountAsync("[Order]"));
        Assert.Equal(10, await Seed.CountAsync("OrderHistory"));

        // Report prodotto, con la riga di Order valorizzata.
        var righeReport = await db.Sql.ScalarAsync<long>("""
            SELECT COUNT_BIG(*) FROM Purge.DryRunReport WHERE RunId = @RunId;
            """, default, SqlParam.Of("@RunId", runId));
        Assert.True(righeReport > 0, "il dry-run non ha prodotto alcuna riga di report");

        var ordiniPrevisti = await db.Sql.ScalarAsync<long>("""
            SELECT RowCountEstimate FROM Purge.DryRunReport
            WHERE RunId = @RunId AND TableName = 'Order';
            """, default, SqlParam.Of("@RunId", runId));
        Assert.Equal(5, ordiniPrevisti);
    }

    /// <summary>Zero candidati e' un esito, non un non-evento: il report va prodotto.</summary>
    [Fact]
    public async Task Dry_run_a_zero_candidati_produce_comunque_il_report()
    {
        await Seed.AddOrderAsync(executionDate: DateTime.Today.AddMonths(-1));   // sotto soglia

        var options = CloneOptions(db.Options, o => o.DryRun = true);
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);

        var righe = await db.Sql.ScalarAsync<long>("""
            SELECT COUNT_BIG(*) FROM Purge.DryRunReport WHERE RunId = @RunId;
            """, default, SqlParam.Of("@RunId", runId));

        Assert.True(righe > 0,
            "a zero candidati il report non e' stato prodotto: 'nessun risultato' " +
            "diventa indistinguibile da 'non eseguito' per chi deve approvarlo");
    }

    // ==================================================================
    // 4. LE VALIDAZIONI
    // ==================================================================

    /// <summary>
    /// V2 deve rilevare un Model che referenzia un candidato.
    ///
    /// I test esistenti verificano solo che le validazioni non diano falsi
    /// positivi sui dati puliti: nessuno le fa fallire, quindi non sappiamo
    /// se rilevino davvero le anomalie che devono rilevare.
    /// </summary>
    [Fact]
    public async Task V2_rileva_il_Model_inserito_dopo_la_selezione()
    {
        var ordine = await Seed.AddOrderAsync(revisions: 1);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);
        var run = await db.Store.LoadAsync(runId, default);

        // Selezione ed espansione: l'ordine entra fra i candidati.
        await PhaseFor(RunPhase.Selecting).ExecuteAsync(run, default);
        run.Phase = RunPhase.Expanding;
        await PhaseFor(RunPhase.Expanding).ExecuteAsync(run, default);
        run.Phase = RunPhase.Validating;

        // Il template compare dopo: e' la cascata C5 che si materializza.
        await Seed.AddModelAsync(ordine);

        var result = await PhaseFor(RunPhase.Validating).ExecuteAsync(run, default);

        Assert.Equal(RunPhase.Failed, result.NextPhase);
        Assert.True(result.Stop);

        var findings = await db.Sql.ScalarAsync<long>("""
            SELECT COUNT_BIG(*) FROM Purge.ValidationFinding
            WHERE RunId = @RunId AND RuleId = 'V2';
            """, default, SqlParam.Of("@RunId", runId));

        Assert.True(findings > 0, "V2 non ha registrato alcun finding");
    }

    // ==================================================================
    // 5. L'HOUSEKEEPING
    // ==================================================================

    /// <summary>
    /// Lo staging di un run concluso e vecchio va rimosso; quello di un run
    /// ancora in esecuzione no.
    ///
    /// Cancellare lo staging di un run riprendibile lo renderebbe
    /// irrecuperabile, perche' il set di candidati e' congelato e non viene
    /// mai ricalcolato.
    /// </summary>
    [Fact]
    public async Task Housekeeping_rimuove_solo_lo_staging_dei_run_conclusi()
    {
        // Run concluso: eseguito e poi invecchiato artificialmente.
        await Seed.AddOrderAsync(revisions: 1);
        var concluso = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);
        await db.Orchestrator.RunAsync(concluso, default);

        await db.Sql.ExecuteAsync("""
            UPDATE Purge.PurgeRun
               SET CompletedOn = DATEADD(DAY, -30, SYSDATETIMEOFFSET())
             WHERE RunId = @RunId;
            """, default, SqlParam.Of("@RunId", concluso));

        // Run ancora in esecuzione, con staging popolato.
        await Seed.AddOrderAsync(revisions: 1);
        var inCorso = await RunUpToPlanningAsync(RetentionStrategy.Terminated);
        await db.Store.SetPhaseAsync(inCorso.RunId, RunPhase.Executing, default);
        await db.Sql.ExecuteAsync("""
            UPDATE Purge.PurgeRun
               SET StartedOn = DATEADD(DAY, -30, SYSDATETIMEOFFSET())
             WHERE RunId = @RunId;
            """, default, SqlParam.Of("@RunId", inCorso.RunId));

        var options = CloneOptions(db.Options, o =>
        {
            o.HousekeepingEnabled = true;
            o.StagingRetentionDays = 7;
            o.FailedStagingRetentionDays = 90;
        });

        var housekeeping = new PurgeHousekeeping(
            db.Sql,
            Microsoft.Extensions.Options.Options.Create(options),
            TimeProvider.System,
            db.Services.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<PurgeHousekeeping>>());

        await housekeeping.RunAsync(default);

        var stagingConcluso = await ContaStagingAsync(concluso);
        var stagingInCorso = await ContaStagingAsync(inCorso.RunId);

        Assert.Equal(0, stagingConcluso);
        Assert.True(stagingInCorso > 0,
            "lo staging di un run ancora in esecuzione e' stato rimosso: " +
            "il run diventerebbe irrecuperabile");
    }

    private Task<long> ContaStagingAsync(Guid runId) =>
        db.Sql.ScalarAsync<long>("""
            SELECT (SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder WHERE RunId = @RunId)
                 + (SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrderHistory WHERE RunId = @RunId);
            """, default, SqlParam.Of("@RunId", runId))!;
}
