using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Il planning legge i candidati a pagine, e il BatchPacker deve sopravvivere
/// al confine fra una pagina e l'altra: il DataReader viene chiuso, il bulk
/// copy usa la stessa connessione, e la lettura riprende dall'ultima chiave.
///
/// E' la parte piu' delicata della paginazione, perche' un collettivo spezzato
/// fra due pagine violerebbe l'atomicita' su cui si regge tutto il resto — e
/// non fallirebbe qui: fallirebbe in esecuzione, dove
/// ValidateCollectiveBatchIntegrity annulla la slice, la riprova finche' i
/// tentativi si esauriscono e poi la abbandona, lasciando l'aggregato a
/// database senza dire perche'.
///
/// A livello unitario il caso non e' riproducibile: per il BatchPacker lo
/// stream e' continuo per costruzione. Serve il database.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class CollectivePagingTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    private BatchPlanner PlannerWithPageSize(int pageSize) =>
        new(db.Sql,
            db.Strategies,
            db.Services.GetRequiredService<ILoggerFactory>().CreateLogger<BatchPlanner>(),
            flushEvery: pageSize);

    /// <summary>
    /// Due collettivi da sei componenti letti a pagine da quattro: la prima
    /// pagina taglia il primo collettivo a meta', la seconda ne contiene la
    /// coda e l'inizio del secondo. Nessun confine cade fra due collettivi.
    /// </summary>
    [Fact]
    public async Task Un_collettivo_a_cavallo_di_due_pagine_resta_in_una_sola_slice()
    {
        const int components = 6;
        const int pageSize = 4;

        var primo = await Seed.AddCollectiveAsync(components: components);
        var secondo = await Seed.AddCollectiveAsync(components: components);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Collective, db.Options, DateTimeOffset.Now, default);
        var run = await db.Store.LoadAsync(runId, default);
        var strategy = db.Strategies.Resolve(run.Strategy);

        Assert.Equal(components * 2, await strategy.SelectAsync(run, default));
        await strategy.ExpandAsync(run, default);

        await PlannerWithPageSize(pageSize).PlanAsync(run, default);

        // Il presupposto del test: il collettivo e' piu' grande di una pagina.
        Assert.True(components > pageSize);

        foreach (var collectiveId in new[] { primo, secondo })
        {
            var batches = await db.Sql.QueryAsync(
                """
                SELECT BatchNo, COUNT_BIG(*) AS Componenti
                FROM Purge.RunCandidateOrder
                WHERE RunId = @RunId AND CollectiveOrderId = @CollectiveOrderId
                GROUP BY BatchNo;
                """,
                r => (BatchNo: r.IsDBNull(0) ? (int?)null : r.GetInt32(0), Componenti: r.GetInt64(1)),
                default,
                SqlParam.Of("@RunId", run.RunId),
                SqlParam.Of("@CollectiveOrderId", collectiveId));

            var batch = Assert.Single(batches);
            Assert.NotNull(batch.BatchNo);
            Assert.Equal(components, batch.Componenti);

            // La testata del collettivo deve finire nella stessa slice dei suoi
            // componenti: e' quella che porta la coda dell'aggregato.
            var headBatchNo = await db.Sql.ScalarAsync<int>(
                """
                SELECT BatchNo
                FROM Purge.RunCandidateCollective
                WHERE RunId = @RunId AND CollectiveOrderId = @CollectiveOrderId;
                """,
                default,
                SqlParam.Of("@RunId", run.RunId),
                SqlParam.Of("@CollectiveOrderId", collectiveId));

            Assert.Equal(batch.BatchNo, headBatchNo);
        }
    }

    /// <summary>
    /// Il planning riparte da zero quando il processo cade in quella fase. Due
    /// pianificazioni consecutive dello stesso run devono produrre lo stesso
    /// piano: se lo mescolassero, il piano approvato in dry-run e quello
    /// eseguito davvero non sarebbero piu' confrontabili.
    /// </summary>
    [Fact]
    public async Task Ripianificare_lo_stesso_run_produce_lo_stesso_piano()
    {
        await Seed.AddCollectiveAsync(components: 6);
        await Seed.AddCollectiveAsync(components: 3);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Collective, db.Options, DateTimeOffset.Now, default);
        var run = await db.Store.LoadAsync(runId, default);
        var strategy = db.Strategies.Resolve(run.Strategy);

        await strategy.SelectAsync(run, default);
        await strategy.ExpandAsync(run, default);

        var planner = PlannerWithPageSize(4);

        var primo = await planner.PlanAsync(run, default);
        var pianoIniziale = await LeggiPianoAsync(run.RunId);

        var secondo = await planner.PlanAsync(run, default);
        var pianoRipetuto = await LeggiPianoAsync(run.RunId);

        Assert.Equal(primo, secondo);
        Assert.Equal(pianoIniziale, pianoRipetuto);
    }

    private Task<List<(Guid OrderId, int BatchNo)>> LeggiPianoAsync(Guid runId) =>
        db.Sql.QueryAsync(
            """
            SELECT OrderId, BatchNo
            FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId AND BatchNo IS NOT NULL
            ORDER BY OrderId;
            """,
            r => (OrderId: r.GetGuid(0), BatchNo: r.GetInt32(1)),
            default,
            SqlParam.Of("@RunId", runId));
}
