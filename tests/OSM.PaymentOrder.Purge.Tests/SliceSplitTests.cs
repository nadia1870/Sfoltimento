using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-11 sul database vero: la bisezione, con la FK reale che rifiuta la
/// cancellazione.
///
/// Il caso e' quello che il disegno assume raro ma non impossibile: fra
/// Expanding ed Executing qualcuno scrive una riga di storico su un ordine
/// gia' candidato. Lo storico non e' nel set congelato, la DELETE del gruppo 2
/// non lo tocca, e la DELETE su Order fallisce con 547. Prima l'intera slice
/// veniva abbandonata: fino a MaxOrdersPerBatch ordini lasciati a database
/// per una riga sola.
///
/// Il dato sporco viene iniettato dopo Planning e prima di Executing, cioe'
/// dopo che V5 (copertura storici) ha gia' dato il via libera. Le fasi si
/// guidano a mano fino a Planning, poi si riprende con l'orchestratore dal
/// checkpoint di Executing, come farebbe la notte successiva.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class SliceSplitTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    private sealed record Progress(
        int BatchNo, string Status, int OrderCount, int? ParentBatchNo, int SplitDepth);

    private Task<List<Progress>> ProgressAsync(Guid runId) =>
        db.Sql.QueryAsync("""
            SELECT BatchNo, Status, OrderCount, ParentBatchNo, SplitDepth
            FROM Purge.RunBatchProgress
            WHERE RunId = @RunId
            ORDER BY BatchNo;
            """,
            r => new Progress(r.GetInt32(0), r.GetString(1), r.GetInt32(2),
                              r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt32(4)),
            default, SqlParam.Of("@RunId", runId));

    /// <summary>Porta il run fino a Planning compreso e lo lascia in Executing, senza eseguire.</summary>
    private async Task<PurgeRun> PianificaAsync(RetentionStrategy strategy)
    {
        var runId = await db.Store.CreateAsync(strategy, db.Options, DateTimeOffset.Now, default);
        var run = await db.Store.LoadAsync(runId, default);
        var s = db.Strategies.Resolve(strategy);

        await s.SelectAsync(run, default);
        await s.ExpandAsync(run, default);
        await db.Planner.PlanAsync(run, default);
        await db.Store.SetPhaseAsync(runId, RunPhase.Executing, default);

        return await db.Store.LoadAsync(runId, default);
    }

    /// <summary>Uno storico fuori dal set congelato: e' cio' che fa fallire la DELETE su Order.</summary>
    private Task SporcaAsync(Guid orderId) =>
        db.Sql.ExecuteAsync("""
            INSERT INTO PaymentOrder.OrderHistory (Id, OrderRefId, Code, StatusCode)
            VALUES (@Id, @OrderRefId, 'LATE', 'Executed');
            """, default, SqlParam.Of("@Id", Guid.NewGuid()), SqlParam.Of("@OrderRefId", orderId));

    private async Task<PurgeRun> RiprendiAsync(Guid runId)
    {
        await db.Orchestrator.RunAsync(runId, default);
        return await db.Store.LoadAsync(runId, default);
    }

    /// <summary>
    /// Sei ordini in una slice, uno sporco. Attesi: cinque cancellati, uno a
    /// database, e la slice abbandonata contiene quell'uno. La madre resta
    /// come traccia in 'Split' e la genealogia porta dal colpevole alla slice
    /// pianificata in origine.
    /// </summary>
    [Fact]
    public async Task Un_ordine_che_rifiuta_la_cancellazione_viene_isolato()
    {
        var ordini = new List<Guid>();
        for (var i = 0; i < 6; i++)
            ordini.Add(await Seed.AddOrderAsync(revisions: 1));

        var run = await PianificaAsync(RetentionStrategy.Terminated);
        var slicePianificate = (await ProgressAsync(run.RunId)).Count;
        Assert.Equal(1, slicePianificate);

        var colpevole = ordini[2];
        await SporcaAsync(colpevole);

        run = await RiprendiAsync(run.RunId);

        Assert.Equal(RunPhase.CompletedWithErrors, run.Phase);
        Assert.Equal(1, await Seed.CountAsync("[Order]"));
        Assert.Equal(colpevole, await db.Sql.ScalarAsync<Guid>(
            "SELECT Id FROM PaymentOrder.[Order];", default));
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());

        var progress = await ProgressAsync(run.RunId);

        var abbandonate = progress.Where(p => p.Status == "Abandoned").ToList();
        var abbandonata = Assert.Single(abbandonate);
        Assert.Equal(1, abbandonata.OrderCount);
        Assert.True(abbandonata.SplitDepth > 0);

        // La madre pianificata e' 'Split', e risalendo per ParentBatchNo
        // dall'abbandonata si arriva a lei.
        Assert.Equal("Split", progress.Single(p => p.BatchNo == 0).Status);
        var corrente = abbandonata;
        while (corrente.ParentBatchNo is { } parent)
        {
            corrente = progress.Single(p => p.BatchNo == parent);
            Assert.Equal("Split", corrente.Status);
        }
        Assert.Equal(0, corrente.BatchNo);

        // Ogni slice completata o abbandonata e' una figlia di qualcuno; i
        // conteggi si conservano: le figlie di una madre coprono i suoi ordini.
        foreach (var madre in progress.Where(p => p.Status == "Split"))
        {
            var figlie = progress.Where(p => p.ParentBatchNo == madre.BatchNo).ToList();
            Assert.Equal(2, figlie.Count);
            Assert.Equal(madre.OrderCount, figlie.Sum(f => f.OrderCount));
            Assert.All(figlie, f => Assert.Equal(madre.SplitDepth + 1, f.SplitDepth));
        }

        var stati = await db.Sql.QueryAsync("""
            SELECT State, COUNT(*) FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId GROUP BY State;
            """, r => (r.GetString(0), r.GetInt32(1)), default, SqlParam.Of("@RunId", run.RunId));
        Assert.Equal(5, stati.Single(s => s.Item1 == "Deleted").Item2);
        Assert.Equal(1, stati.Single(s => s.Item1 == "Failed").Item2);

        // L'audit conta solo cio' che e' stato committato: cinque testate.
        var auditOrder = await db.Sql.ScalarAsync<long>("""
            SELECT ISNULL(SUM(RowsDeleted), 0) FROM Purge.PurgeAudit
            WHERE RunId = @RunId AND TableName = 'Order';
            """, default, SqlParam.Of("@RunId", run.RunId));
        Assert.Equal(5, auditOrder);
    }

    /// <summary>
    /// L'unita' indivisibile e' l'aggregato. Due collettivi da tre in una
    /// slice, un componente sporco: la divisione separa i collettivi, quello
    /// pulito viene cancellato, quello sporco viene abbandonato intero — tre
    /// ordini, non uno — perche' non si puo' dividere oltre senza rompere
    /// l'atomicita' che ValidateCollectiveBatchIntegrity presidia.
    /// </summary>
    [Fact]
    public async Task Un_collettivo_non_viene_mai_diviso()
    {
        var sporco = await Seed.AddCollectiveAsync(components: 3);
        var pulito = await Seed.AddCollectiveAsync(components: 3);

        var run = await PianificaAsync(RetentionStrategy.Collective);
        Assert.Single(await ProgressAsync(run.RunId));

        var componente = await db.Sql.ScalarAsync<Guid>("""
            SELECT TOP (1) cgo.OrderId
            FROM PaymentOrder.CollectiveOrderGroupOrder AS cgo
            INNER JOIN PaymentOrder.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
            WHERE g.CollectiveOrderId = @Id ORDER BY cgo.OrderId;
            """, default, SqlParam.Of("@Id", sporco));
        await SporcaAsync(componente);

        run = await RiprendiAsync(run.RunId);

        Assert.Equal(RunPhase.CompletedWithErrors, run.Phase);
        Assert.Equal(1, await Seed.CountAsync("CollectiveOrder"));
        Assert.Equal(sporco, await db.Sql.ScalarAsync<Guid>(
            "SELECT Id FROM PaymentOrder.CollectiveOrder;", default));
        Assert.Equal(3, await Seed.CountAsync("[Order]"));
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());

        var progress = await ProgressAsync(run.RunId);
        var abbandonata = Assert.Single(progress, p => p.Status == "Abandoned");
        Assert.Equal(3, abbandonata.OrderCount);
        Assert.Equal(1, abbandonata.SplitDepth);
        Assert.Equal(0, abbandonata.ParentBatchNo);

        var completata = Assert.Single(progress, p => p.Status == "Completed");
        Assert.Equal(3, completata.OrderCount);

        // Nessun collettivo spezzato fra due slice: la guardia in esecuzione
        // non ha mai avuto motivo di scattare.
        var spezzati = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM (
                SELECT CollectiveOrderId FROM Purge.RunCandidateOrder
                WHERE RunId = @RunId GROUP BY CollectiveOrderId
                HAVING COUNT(DISTINCT BatchNo) > 1) AS x;
            """, default, SqlParam.Of("@RunId", run.RunId));
        Assert.Equal(0, spezzati);

        var testataPulita = await db.Sql.ScalarAsync<string>("""
            SELECT State FROM Purge.RunCandidateCollective
            WHERE RunId = @RunId AND CollectiveOrderId = @Id;
            """, default, SqlParam.Of("@RunId", run.RunId), SqlParam.Of("@Id", pulito));
        Assert.Equal("Deleted", testataPulita);
    }

    /// <summary>
    /// Anche gli storici orfani si dividono, per singolo storico. Nello schema
    /// di prova non c'e' modo di far rifiutare la cancellazione a un orfano —
    /// le sole tabelle che lo referenziano sono nella topologia e vengono
    /// cancellate prima — quindi qui si prova lo statement direttamente: la
    /// divisione produce due figlie che coprono tutti gli storici, e il run
    /// ripreso le esegue entrambe fino in fondo.
    /// </summary>
    [Fact]
    public async Task Gli_storici_orfani_si_dividono_per_storico()
    {
        for (var i = 0; i < 5; i++)
            await Seed.AddOrphanHistoryAsync();

        var run = await PianificaAsync(RetentionStrategy.OrphanHistory);
        Assert.Single(await ProgressAsync(run.RunId));

        var figlie = await db.Store.SplitSliceAsync(run.RunId, 0, "prova", default);
        Assert.Equal(2, figlie);

        var progress = await ProgressAsync(run.RunId);
        Assert.Equal("Split", progress.Single(p => p.BatchNo == 0).Status);
        var pendenti = progress.Where(p => p.Status == "Pending").ToList();
        Assert.Equal(2, pendenti.Count);
        Assert.Equal(5, pendenti.Sum(p => p.OrderCount));
        Assert.All(pendenti, p => { Assert.Equal(0, p.ParentBatchNo); Assert.Equal(1, p.SplitDepth); });

        // Nessuno storico e' rimasto sulla madre.
        var sullaMadre = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.RunCandidateOrderHistory
            WHERE RunId = @RunId AND BatchNo = 0;
            """, default, SqlParam.Of("@RunId", run.RunId));
        Assert.Equal(0, sullaMadre);

        run = await RiprendiAsync(run.RunId);

        Assert.Equal(RunPhase.Completed, run.Phase);
        Assert.Equal(0, await Seed.CountAsync("OrderHistory"));
    }

    /// <summary>
    /// Una slice da un aggregato solo non si divide: lo statement non tocca
    /// niente e restituisce zero, e lo stato resta 'Pending'.
    /// </summary>
    [Fact]
    public async Task Una_slice_da_un_aggregato_non_si_divide()
    {
        await Seed.AddCollectiveAsync(components: 3);

        var run = await PianificaAsync(RetentionStrategy.Collective);

        var figlie = await db.Store.SplitSliceAsync(run.RunId, 0, "prova", default);

        Assert.Equal(0, figlie);
        var unica = Assert.Single(await ProgressAsync(run.RunId));
        Assert.Equal("Pending", unica.Status);
        Assert.Equal(3, unica.OrderCount);
    }
}
