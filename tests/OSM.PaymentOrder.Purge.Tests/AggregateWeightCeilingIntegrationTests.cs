using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-17 sul database: il percorso SQL dell'esclusione per peso.
///
/// I test in memoria coprono la decisione del packer, ma non ciò che accade
/// dopo: la colonna nella tabella temporanea, il mapping della bulk copy, la
/// UPDATE che porta il candidato in 'Excluded' e i filtri aggiunti alle altre
/// due UPDATE. È esattamente il tratto in cui si annidava un mapping
/// dimenticato, emerso solo perché faceva fallire tutto il resto: senza questi
/// test, un difetto lì si manifesterebbe la prima notte in cui un aggregato
/// supera la soglia — cioè nel caso patologico che il tetto dovrebbe rendere
/// gestibile.
///
/// La fixture registra il planner con il tetto disattivato, perché con
/// MaxRowsPerBatch = 50 un tetto proporzionato scatterebbe su seed che non lo
/// riguardano. Qui il planner viene costruito a mano con il tetto voluto.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class AggregateWeightCeilingIntegrationTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    /// <summary>Planner con il tetto voluto: quello iniettato dalla fixture ha il tetto a zero.</summary>
    private BatchPlanner PlannerCon(int tetto) =>
        new(db.Sql, db.Strategies, NullLogger<BatchPlanner>.Instance, maxAggregateWeight: tetto);

    /// <summary>Porta il run fino al planning compreso, con un tetto scelto.</summary>
    private async Task<PurgeRun> PianificaAsync(RetentionStrategy strategy, int tetto)
    {
        var runId = await db.Store.CreateAsync(strategy, db.Options, DateTimeOffset.Now, default);
        var run = await db.Store.LoadAsync(runId, default);
        var s = db.Strategies.Resolve(strategy);

        await s.SelectAsync(run, default);
        await s.ExpandAsync(run, default);
        await PlannerCon(tetto).PlanAsync(run, default);

        return run;
    }

    private sealed record Candidato(string State, string? Reason, int? BatchNo, int? RowWeight);

    private Task<List<Candidato>> CandidatiAsync(Guid runId) =>
        db.Sql.QueryAsync("""
            SELECT State, ExcludedReason, BatchNo, RowWeight
            FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId;
            """,
            r => new Candidato(r.GetString(0),
                               r.IsDBNull(1) ? null : r.GetString(1),
                               r.IsDBNull(2) ? null : r.GetInt32(2),
                               r.IsDBNull(3) ? null : r.GetInt32(3)),
            default, SqlParam.Of("@RunId", runId));

    /// <summary>
    /// Un ordine con molte revisioni supera il tetto: resta a database, il suo
    /// candidato passa a 'Excluded' senza slice, e gli ordini normali dello
    /// stesso run vengono pianificati normalmente.
    /// </summary>
    [Fact]
    public async Task Un_ordine_oltre_il_tetto_e_escluso_e_non_riceve_una_slice()
    {
        const int tetto = 50;   // pari a MaxRowsPerBatch della fixture

        var normale = await Seed.AddOrderAsync(revisions: 1);              // peso 3
        var pesante = await Seed.AddOrderAsync(revisions: 30);             // peso 61

        var run = await PianificaAsync(RetentionStrategy.Terminated, tetto);
        var candidati = await CandidatiAsync(run.RunId);

        var escluso = Assert.Single(candidati, c => c.State == "Excluded");
        Assert.Equal("AggregateTooLarge", escluso.Reason);
        Assert.Null(escluso.BatchNo);
        Assert.True(escluso.RowWeight > tetto);

        var selezionato = Assert.Single(candidati, c => c.State == "Selected");
        Assert.NotNull(selezionato.BatchNo);

        // Gli storici dell'ordine escluso non devono ricevere una slice:
        // finirebbero in una transazione che non contiene la loro testata.
        var storiciDelPesante = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.RunCandidateOrderHistory
            WHERE RunId = @RunId AND OrderId = @OrderId AND BatchNo IS NOT NULL;
            """, default, SqlParam.Of("@RunId", run.RunId), SqlParam.Of("@OrderId", pesante));
        Assert.Equal(0, storiciDelPesante);

        var storiciConSlice = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.RunCandidateOrderHistory
            WHERE RunId = @RunId AND OrderId = @OrderId AND BatchNo IS NOT NULL;
            """, default, SqlParam.Of("@RunId", run.RunId), SqlParam.Of("@OrderId", normale));
        Assert.Equal(1, storiciConSlice);

        // Nessun candidato selezionato resta senza slice: l'esclusione non
        // deve confondersi con un'assegnazione mancata.
        Assert.Equal(0, await db.Sql.ScalarAsync<long>(
            RetentionSql.CountUnassigned, default, SqlParam.Of("@RunId", run.RunId)));
    }

    /// <summary>
    /// L'esecuzione cancella solo ciò che ha una slice: l'aggregato escluso
    /// resta a database, ed è il punto dell'intera decisione.
    /// </summary>
    [Fact]
    public async Task L_aggregato_escluso_non_viene_cancellato()
    {
        var normale = await Seed.AddOrderAsync(revisions: 1);
        var pesante = await Seed.AddOrderAsync(revisions: 30);

        var run = await PianificaAsync(RetentionStrategy.Terminated, tetto: 50);
        await db.Store.SetPhaseAsync(run.RunId, RunPhase.Executing, default);
        await db.Orchestrator.RunAsync(run.RunId, default);

        var rimasti = await db.Sql.QueryAsync(
            "SELECT Id FROM PaymentOrder.[Order];", r => r.GetGuid(0), default);

        Assert.Equal(pesante, Assert.Single(rimasti));
        Assert.Equal(0, await db.Sql.ScalarAsync<int>(
            "SELECT COUNT(*) FROM PaymentOrder.OrderHistory WHERE OrderRefId = @Id;",
            default, SqlParam.Of("@Id", normale)));
    }

    /// <summary>
    /// Un collettivo si misura sul peso complessivo e viene escluso intero,
    /// testata compresa: la testata non deve finire in una slice che non
    /// contiene i suoi componenti.
    /// </summary>
    [Fact]
    public async Task Un_collettivo_oltre_il_tetto_e_escluso_con_la_sua_testata()
    {
        // Il peso del collettivo e' la somma dei componenti, non quello del
        // singolo: venti componenti da tre righe pesano sessanta, sopra il
        // tetto, mentre nessuno di loro lo supererebbe da solo.
        //
        // Il tetto non puo' essere abbassato sotto MaxRowsPerBatch — il
        // costruttore lo rifiuta, perche' renderebbe indecidibile il caso
        // intermedio — quindi e' l'aggregato a doversi far pesante.
        const int componenti = 20;
        var collettivo = await Seed.AddCollectiveAsync(components: componenti);

        var run = await PianificaAsync(RetentionStrategy.Collective, tetto: 50);
        var candidati = await CandidatiAsync(run.RunId);

        Assert.Equal(componenti, candidati.Count);
        Assert.All(candidati, c =>
        {
            Assert.Equal("Excluded", c.State);
            Assert.Equal("AggregateTooLarge", c.Reason);
            Assert.Null(c.BatchNo);
        });

        var batchTestata = await db.Sql.ScalarAsync<int?>("""
            SELECT BatchNo FROM Purge.RunCandidateCollective
            WHERE RunId = @RunId AND CollectiveOrderId = @Id;
            """, default, SqlParam.Of("@RunId", run.RunId), SqlParam.Of("@Id", collettivo));
        Assert.Null(batchTestata);
    }

    /// <summary>
    /// Il report dice a chi lo approva che cosa NON verra' cancellato: un
    /// conteggio di righe rimosse, da solo, non lo direbbe.
    /// </summary>
    [Fact]
    public async Task Il_report_censisce_gli_aggregati_esclusi()
    {
        await Seed.AddOrderAsync(revisions: 1);
        await Seed.AddOrderAsync(revisions: 30);

        var run = await PianificaAsync(RetentionStrategy.Terminated, tetto: 50);
        var report = await db.Services.GetRequiredService<DryRunReporter>()
                              .ProduceAsync(run, default);

        Assert.Equal(1, report.ExcludedAggregates);
        Assert.True(report.ExcludedAggregateMaxWeight > 50);

        var testo = report.ToText();
        Assert.Contains("Aggregati oltre il tetto", testo);
    }

    /// <summary>Con il tetto disattivato nulla cambia: e' il comportamento precedente.</summary>
    [Fact]
    public async Task Con_il_tetto_disattivato_nessun_aggregato_viene_escluso()
    {
        await Seed.AddOrderAsync(revisions: 30);

        var run = await PianificaAsync(RetentionStrategy.Terminated, tetto: 0);
        var candidato = Assert.Single(await CandidatiAsync(run.RunId));

        Assert.Equal("Selected", candidato.State);
        Assert.NotNull(candidato.BatchNo);
    }
}
