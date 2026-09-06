using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La selezione procede a pagine, con una filigrana che avanza sulla stessa
/// chiave dell'indice di supporto.
///
/// Il rischio della paginazione a chiave e' che una pagina salti righe al
/// confine, o le riprenda due volte. Questi test seminano piu' righe della
/// dimensione di pagina configurata dalla fixture e verificano che il set
/// risultante coincida con quello che produrrebbe uno statement unico.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class BatchedSelectionTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    /// <summary>
    /// Nessun candidato deve andare perso al confine fra una pagina e l'altra.
    /// Con SelectionBatchSize a 7 e trenta ordini eleggibili il ciclo compie
    /// cinque giri: se la filigrana avanzasse di una posizione di troppo, il
    /// primo ordine di ogni pagina resterebbe fuori.
    /// </summary>
    [Fact]
    public async Task La_paginazione_non_perde_candidati()
    {
        const int ordini = 30;
        for (var i = 0; i < ordini; i++)
            await Seed.AddOrderAsync(revisions: 2);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);

        Assert.Equal(0, await Seed.CountAsync("[Order]"));
        Assert.Equal(0, await Seed.CountAsync("OrderHistory"));
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());
    }

    /// <summary>
    /// Ordini che condividono la stessa data di ancoraggio non devono
    /// confondere la filigrana: e' il caso in cui la chiave (Anchor, Id) serve
    /// davvero, perche' la sola data non discrimina. Un caricamento massivo
    /// produce esattamente questa distribuzione.
    /// </summary>
    [Fact]
    public async Task Date_identiche_non_confondono_la_filigrana()
    {
        var stessaData = DateTime.Today.AddYears(-8);
        for (var i = 0; i < 25; i++)
            await Seed.AddOrderAsync(revisions: 1, executionDate: stessaData);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);

        Assert.Equal(0, await Seed.CountAsync("[Order]"));
    }

    /// <summary>
    /// La selezione e' idempotente. Rieseguirla sullo stesso run, come accade
    /// dopo un'interruzione a meta' ciclo, non deve produrre candidati doppi:
    /// il NOT EXISTS sullo staging esiste per questo, e la chiave primaria
    /// (RunId, OrderId) lo dimostrerebbe fallendo.
    /// </summary>
    [Fact]
    public async Task Rieseguire_la_selezione_non_duplica_i_candidati()
    {
        for (var i = 0; i < 20; i++)
            await Seed.AddOrderAsync(revisions: 2);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        var run = await db.Store.LoadAsync(runId, default);
        var strategia = db.Strategies.Resolve(RetentionStrategy.Terminated);

        var primo = await strategia.SelectAsync(run, default);
        var secondo = await strategia.SelectAsync(run, default);

        var candidati = await db.Sql.ScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(20, primo);
        Assert.Equal(0, secondo);
        Assert.Equal(20, candidati);
    }

    /// <summary>
    /// Espansione e calcolo del peso girano ora sulla stessa pagina. Ogni
    /// candidato deve uscire con un peso valorizzato: uno rimasto NULL
    /// significa una pagina saltata, e il pianificatore lo tratterebbe come
    /// peso 1 sballando il dimensionamento delle slice.
    /// </summary>
    [Fact]
    public async Task Ogni_candidato_esce_dall_espansione_con_un_peso()
    {
        for (var i = 0; i < 22; i++)
            await Seed.AddOrderAsync(revisions: 3);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        var run = await db.Store.LoadAsync(runId, default);
        var strategia = db.Strategies.Resolve(RetentionStrategy.Terminated);

        await strategia.SelectAsync(run, default);
        await strategia.ExpandAsync(run, default);

        var senzaPeso = await db.Sql.ScalarAsync<long>(
            """
            SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId AND State = 'Selected' AND RowWeight IS NULL;
            """,
            default, SqlParam.Of("@RunId", runId));

        var storiciMancanti = await db.Sql.ScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM PaymentOrder.OrderHistory AS oh
            INNER JOIN Purge.RunCandidateOrder AS c
                    ON c.OrderId = oh.OrderRefId AND c.RunId = @RunId
            WHERE NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrderHistory AS h
                              WHERE h.RunId = @RunId AND h.OrderHistoryId = oh.Id);
            """,
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(0, senzaPeso);
        Assert.Equal(0, storiciMancanti);
    }

    /// <summary>
    /// I piani ricorrenti paginano su StandingOrder.LastExecutionDate, non su
    /// Order: e' l'unica selezione la cui filigrana non sta sulla tabella in
    /// cui vengono inseriti i candidati.
    /// </summary>
    [Fact]
    public async Task La_selezione_dei_piani_ricorrenti_pagina_correttamente()
    {
        for (var i = 0; i < 18; i++)
            await Seed.AddOrderAsync(
                standingOrder: true,
                lastExecutionDate: DateTime.Today.AddYears(-7),
                detailTable: "StandingOrder");

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.StandingOrders, db.Options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);

        Assert.Equal(0, await Seed.CountAsync("StandingOrder"));
        Assert.Equal(0, await Seed.CountAsync("[Order]"));
    }
}
