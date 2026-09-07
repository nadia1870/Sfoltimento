using Microsoft.Data.SqlClient;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine.Phases;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Come finisce un run.
///
/// Prima esistevano due esiti: Completed e Failed. Failed era terminale ed era
/// anche l'esito di una rete caduta per un secondo, quindi un guasto passeggero
/// distruggeva un set di candidati congelato. E un run che abbandonava delle
/// slice chiudeva in Completed come uno che non aveva lasciato niente indietro.
///
/// Un guasto vero non si riproduce a comando, quindi le fasi vengono sostituite
/// con implementazioni che sollevano l'eccezione voluta.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class RunLifecycleTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    /// <summary>Fase che fallisce sempre nel modo indicato.</summary>
    private sealed class FaseCheEsplode(RunPhase fase, Func<Exception> guasto) : IPurgePhase
    {
        public RunPhase Phase => fase;
        public IReadOnlySet<RunPhase> HandledPhases { get; } = fase == RunPhase.Selecting?
            new HashSet<RunPhase> { RunPhase.Created, RunPhase.Selecting }:
            new HashSet<RunPhase> { fase };

        public Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct) =>
            throw guasto();
    }

    private async Task<SqlException> ConnessioneCadutaAsync()
    {
        // SqlException non si costruisce: ha solo costruttori interni. Se ne
        // provoca una vera puntando allo stesso server su un database che non
        // esiste, che risponde 4060 — uno dei numeri che SqlErrors considera
        // guasto e non difetto.
        var cs = new SqlConnectionStringBuilder(db.ConnectionString)
        {
            InitialCatalog = "database_che_non_esiste_" + Guid.NewGuid().ToString("N")[..8],
            ConnectTimeout = 5
        }.ConnectionString;

        try
        {
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
        }
        catch (SqlException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            "La connessione e' riuscita: il test non puo' provare il guasto.");
    }

    private async Task<Guid> CreaRunAsync()
    {
        await Seed.AddOrderAsync(revisions: 1);
        return await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);
    }

    private async Task<int> InterruzioniAsync(Guid runId) =>
        await db.Sql.ScalarAsync<int>(
            "SELECT InterruptionCount FROM Purge.PurgeRun WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

    // ---------------------------------------------------------------- guasti

    /// <summary>
    /// Un guasto di infrastruttura non deve chiudere il run: la fase resta il
    /// checkpoint, e la ricerca dei run riprendibili lo ritrova.
    /// </summary>
    [Fact]
    public async Task Un_guasto_lascia_il_run_riprendibile()
    {
        var runId = await CreaRunAsync();
        var orchestratore = db.OrchestratorWith(
            new FaseCheEsplode(RunPhase.Selecting, () => new TimeoutException("rete")));

        await Assert.ThrowsAsync<TimeoutException>(
            () => orchestratore.RunAsync(runId, default));

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Selecting, run.Phase);
        Assert.Equal(1, await InterruzioniAsync(runId));
        Assert.Equal(runId, await db.Store.FindResumableAsync(
            RetentionStrategy.Terminated, default));
    }

    /// <summary>
    /// Un errore di programma invece chiude il run. Nel dubbio si classifica
    /// cosi': fermarsi e chiedere aiuto e' meno grave che riprovare
    /// all'infinito una cancellazione sbagliata.
    /// </summary>
    [Fact]
    public async Task Un_errore_logico_chiude_il_run()
    {
        var runId = await CreaRunAsync();
        var orchestratore = db.OrchestratorWith(
            new FaseCheEsplode(RunPhase.Selecting,
                () => new InvalidOperationException("topologia incoerente")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestratore.RunAsync(runId, default));

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Failed, run.Phase);
        Assert.Equal(0, await InterruzioniAsync(runId));
        Assert.Null(await db.Store.FindResumableAsync(RetentionStrategy.Terminated, default));
    }

    /// <summary>
    /// Una SqlException con un numero transitorio e' un guasto, non un errore.
    /// E' il caso che si presenta davvero alle due di notte.
    /// </summary>
    [Fact]
    public async Task Una_SqlException_transitoria_e_un_guasto()
    {
        var guasto = await ConnessioneCadutaAsync();

        var runId = await CreaRunAsync();
        var orchestratore = db.OrchestratorWith(
            new FaseCheEsplode(RunPhase.Selecting, () => guasto));

        await Assert.ThrowsAsync<SqlException>(() => orchestratore.RunAsync(runId, default));

        var run = await db.Store.LoadAsync(runId, default);

        Assert.NotEqual(RunPhase.Failed, run.Phase);
        Assert.Equal(1, await InterruzioniAsync(runId));
    }

    /// <summary>
    /// Un guasto che non passa non deve far ripartire lo stesso run ogni notte
    /// per sempre. Oltre la soglia il run viene dichiarato fallito e chiede
    /// attenzione invece di riprovare in silenzio.
    /// </summary>
    [Fact]
    public async Task Oltre_il_limite_di_riprese_il_run_viene_chiuso()
    {
        var runId = await CreaRunAsync();
        var orchestratore = db.OrchestratorWith(
            new FaseCheEsplode(RunPhase.Selecting, () => new TimeoutException("rete")));

        for (var i = 0; i < db.Options.MaxRunInterruptions; i++)
            await Assert.ThrowsAsync<TimeoutException>(
                () => orchestratore.RunAsync(runId, default));

        Assert.Equal(db.Options.MaxRunInterruptions, await InterruzioniAsync(runId));

        // Il giro successivo non solleva: si accorge del limite e chiude.
        await orchestratore.RunAsync(runId, default);

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Failed, run.Phase);
        Assert.Null(await db.Store.FindResumableAsync(RetentionStrategy.Terminated, default));
    }

    /// <summary>
    /// La chiusura della finestra operativa e' il funzionamento previsto, non
    /// un guasto: non deve consumare una ripresa.
    /// </summary>
    [Fact]
    public async Task La_cancellazione_non_conta_come_interruzione()
    {
        var runId = await CreaRunAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var orchestratore = db.OrchestratorWith(
            new FaseCheEsplode(RunPhase.Selecting, () => new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestratore.RunAsync(runId, default));

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Selecting, run.Phase);
        Assert.Equal(0, await InterruzioniAsync(runId));
    }

    // --------------------------------------------------- conclusione parziale

    /// <summary>
    /// Un run che non lascia niente indietro chiude in Completed. E' il caso
    /// normale, e serve come riferimento per il successivo.
    /// </summary>
    [Fact]
    public async Task Un_run_pulito_chiude_in_Completed()
    {
        for (var i = 0; i < 5; i++)
            await Seed.AddOrderAsync(revisions: 2);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Completed, run.Phase);
        Assert.Equal(0, await Seed.CountAsync("[Order]"));
    }

    /// <summary>
    /// Un run con slice abbandonate non e' concluso allo stesso modo: restano
    /// aggregati a database che nessuno ha cancellato. Chiudeva in Completed e
    /// la differenza viveva in una riga di log.
    /// </summary>
    [Fact]
    public async Task Un_run_con_slice_abbandonate_chiude_in_CompletedWithErrors()
    {
        for (var i = 0; i < 4; i++)
            await Seed.AddOrderAsync(revisions: 1);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        var run = await db.Store.LoadAsync(runId, default);
        var strategia = db.Strategies.Resolve(RetentionStrategy.Terminated);

        await strategia.SelectAsync(run, default);
        await strategia.ExpandAsync(run, default);
        await db.Planner.PlanAsync(run, default);

        // Una slice viene marcata abbandonata a mano: provocare un abbandono
        // vero richiederebbe un guasto ripetuto, che questo test non sta
        // verificando.
        await db.Sql.ExecuteAsync("""
            UPDATE TOP (1) Purge.RunBatchProgress
               SET Status = 'Abandoned', LastError = 'simulato'
             WHERE RunId = @RunId AND Status = 'Pending';
            """, default, SqlParam.Of("@RunId", runId));

        await db.Store.SetPhaseAsync(runId, RunPhase.Executing, default);
        await db.Orchestrator.RunAsync(runId, default);

        var finale = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.CompletedWithErrors, finale.Phase);
        Assert.Null(await db.Store.FindResumableAsync(RetentionStrategy.Terminated, default));
    }

    /// <summary>
    /// Il conteggio degli abbandoni viene dal database, non dal contatore
    /// dell'ultima sessione: un run che abbandona una notte e completa quella
    /// dopo chiuderebbe altrimenti come pulito.
    /// </summary>
    [Fact]
    public async Task Gli_abbandoni_di_una_sessione_precedente_contano_ancora()
    {
        var runId = await CreaRunAsync();

        await db.Sql.ExecuteAsync("""
            INSERT INTO Purge.RunBatchProgress
                (RunId, BatchNo, Status, IsOversized, OrderCount, EstimatedRowCount)
            VALUES (@RunId, 99, 'Abandoned', 0, 1, 1);
            """, default, SqlParam.Of("@RunId", runId));

        Assert.Equal(1, await db.Store.CountAbandonedSlicesAsync(runId, default));
    }

    // ------------------------------------------------------------ terminalita'

    /// <summary>
    /// CompletedWithErrors e' terminale: un run in quello stato non deve essere
    /// ripreso, e l'elenco delle fasi terminali deve derivare dall'enum invece
    /// di essere riscritto in ogni query.
    /// </summary>
    [Theory]
    [InlineData(RunPhase.Completed)]
    [InlineData(RunPhase.CompletedWithErrors)]
    [InlineData(RunPhase.Failed)]
    [InlineData(RunPhase.Aborted)]
    public async Task Le_fasi_terminali_non_sono_riprendibili(RunPhase fase)
    {
        var runId = await CreaRunAsync();
        await db.Store.SetPhaseAsync(runId, fase, default);

        Assert.True(RunPhases.IsTerminal(fase));
        Assert.Null(await db.Store.FindResumableAsync(RetentionStrategy.Terminated, default));
    }

    /// <summary>
    /// La colonna Phase deve contenere il nome piu' lungo senza troncarlo. Un
    /// troncamento silenzioso renderebbe la fase irriconoscibile all'Enum.Parse
    /// del caricamento, e il run illeggibile.
    /// </summary>
    [Fact]
    public async Task La_colonna_Phase_regge_il_nome_piu_lungo()
    {
        var piuLunga = RunPhases.TerminalNames.MaxBy(n => n.Length)!;

        var runId = await CreaRunAsync();
        await db.Store.SetPhaseAsync(
            runId, Enum.Parse<RunPhase>(piuLunga), default);

        var salvata = await db.Sql.ScalarAsync<string>(
            "SELECT Phase FROM Purge.PurgeRun WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(piuLunga, salvata);
    }
}
