using Microsoft.Extensions.DependencyInjection;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-10: un collettivo che non si puo' cancellare viene censito ed escluso in
/// selezione, e il run prosegue sugli altri.
///
/// Prima di questa decisione l'anomalia arrivava in Validating, che e'
/// fail-hard: un solo collettivo con un componente referenziato da Model
/// mandava l'intero run in Failed, e la notte dopo un run nuovo falliva allo
/// stesso punto. Nessun collettivo veniva sfoltito finche' qualcuno non
/// correggeva i dati a mano.
///
/// L'appartenenza ambigua (un ordine in due collettivi) non e' riproducibile
/// qui: lo schema di prova ha un indice univoco su
/// CollectiveOrderGroupOrder.OrderId, come quello reale dovrebbe. La UPDATE
/// esiste come difesa e la post-condizione in CollectiveStrategy la copre.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class CollectiveExclusionTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    private async Task<PurgeRun> RunCollectiveAsync()
    {
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Collective, db.Options, DateTimeOffset.Now, default);
        await db.Orchestrator.RunAsync(runId, default);
        return await db.Store.LoadAsync(runId, default);
    }

    private Task<List<(Guid CollectiveOrderId, string State, string? Reason)>> CensimentoAsync(Guid runId) =>
        db.Sql.QueryAsync("""
            SELECT CollectiveOrderId, State, ExcludedReason
            FROM Purge.RunCandidateCollective
            WHERE RunId = @RunId;
            """,
            r => (r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
            default, SqlParam.Of("@RunId", runId));

    private async Task<Guid> PrimoComponenteAsync(Guid collectiveId) =>
        (await db.Sql.QueryAsync("""
            SELECT TOP (1) cgo.OrderId
            FROM PaymentOrder.CollectiveOrderGroupOrder AS cgo
            INNER JOIN PaymentOrder.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
            WHERE g.CollectiveOrderId = @Id AND cgo.OrderId IS NOT NULL
            ORDER BY cgo.OrderId;
            """, r => r.GetGuid(0), default, SqlParam.Of("@Id", collectiveId))).Single();

    /// <summary>
    /// Un componente con modello: il suo collettivo resta a database, censito;
    /// l'altro viene cancellato; il run chiude Completed, non Failed, e la
    /// validazione V2 non ha niente da segnalare perche' l'esclusione e'
    /// avvenuta a monte.
    /// </summary>
    [Fact]
    public async Task Un_componente_con_modello_esclude_il_suo_collettivo_e_il_run_prosegue()
    {
        var anomalo = await Seed.AddCollectiveAsync(components: 2);
        var pulito = await Seed.AddCollectiveAsync(components: 2);
        await Seed.AddModelAsync(await PrimoComponenteAsync(anomalo));

        var run = await RunCollectiveAsync();

        Assert.Equal(RunPhase.Completed, run.Phase);
        Assert.Equal(1, await Seed.CountAsync("CollectiveOrder"));
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());

        var censimento = await CensimentoAsync(run.RunId);
        var a = censimento.Single(c => c.CollectiveOrderId == anomalo);
        Assert.Equal("Excluded", a.State);
        Assert.Equal("ComponentHasModel", a.Reason);
        Assert.Equal("Deleted", censimento.Single(c => c.CollectiveOrderId == pulito).State);

        // Nessun componente del collettivo escluso e' entrato nei candidati.
        var componentiAnomali = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId AND CollectiveOrderId = @Id;
            """, default, SqlParam.Of("@RunId", run.RunId), SqlParam.Of("@Id", anomalo));
        Assert.Equal(0, componentiAnomali);

        var findingV2 = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.ValidationFinding
            WHERE RunId = @RunId AND RuleId = 'V2';
            """, default, SqlParam.Of("@RunId", run.RunId));
        Assert.Equal(0, findingV2);

        // Il report riporta l'esclusione con il motivo: chi approva deve vederla.
        var report = await db.Services.GetRequiredService<DryRunReporter>()
            .ProduceAsync(run, default);
        Assert.Equal(1, report.ExcludedCollectives["ComponentHasModel"]);
        Assert.Contains("ComponentHasModel", report.ToText());
    }

    /// <summary>
    /// Caso C7: uno storico di dettaglio di un componente punta al dettaglio
    /// di un ordine fuori dal collettivo. La slice cancellerebbe lo storico ma
    /// non il dettaglio, quindi V1 fallirebbe il run. Il collettivo viene
    /// escluso con il nome della tabella nel motivo.
    /// </summary>
    [Fact]
    public async Task Uno_storico_che_punta_fuori_dal_collettivo_lo_esclude()
    {
        var anomalo = await Seed.AddCollectiveAsync(components: 2);
        var pulito = await Seed.AddCollectiveAsync(components: 2);

        // Un ordine estraneo, con il suo dettaglio: e' il bersaglio del riferimento.
        var estraneo = await Seed.AddOrderAsync();
        var dettaglioEstraneo = await db.Sql.ScalarAsync<Guid>(
            "SELECT Id FROM PaymentOrder.BankTransfer WHERE OrderId = @Id;",
            default, SqlParam.Of("@Id", estraneo));

        var componente = await PrimoComponenteAsync(anomalo);
        var storicoId = Guid.NewGuid();
        await db.Sql.ExecuteAsync("""
            INSERT INTO PaymentOrder.OrderHistory (Id, OrderRefId, Code, StatusCode)
            VALUES (@Id, @OrderRefId, 'XREF', 'Executed');
            INSERT INTO PaymentOrder.BankTransferHistory (Id, OrderHistoryId, BankTransferRefId)
            VALUES (@DetailHistoryId, @Id, @RefId);
            """, default,
            SqlParam.Of("@Id", storicoId), SqlParam.Of("@OrderRefId", componente),
            SqlParam.Of("@DetailHistoryId", Guid.NewGuid()), SqlParam.Of("@RefId", dettaglioEstraneo));

        var run = await RunCollectiveAsync();

        Assert.Equal(RunPhase.Completed, run.Phase);
        Assert.Equal(1, await Seed.CountAsync("CollectiveOrder"));
        Assert.Equal(0, await Seed.CountOrphanRowsAsync());

        var censimento = await CensimentoAsync(run.RunId);
        var a = censimento.Single(c => c.CollectiveOrderId == anomalo);
        Assert.Equal("Excluded", a.State);
        Assert.Equal(RetentionSql.CrossReferenceReasonPrefix + "BankTransferHistory", a.Reason);
        Assert.Equal("Deleted", censimento.Single(c => c.CollectiveOrderId == pulito).State);

        var findingV1 = await db.Sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.ValidationFinding
            WHERE RunId = @RunId AND RuleId = 'V1';
            """, default, SqlParam.Of("@RunId", run.RunId));
        Assert.Equal(0, findingV1);
    }

    /// <summary>
    /// Le esclusioni sono idempotenti: la selezione riparte da capo dopo
    /// un'interruzione, e la seconda passata non deve ne' duplicare ne'
    /// riportare in 'Selected' cio' che aveva escluso.
    /// </summary>
    [Fact]
    public async Task Riselezionare_lo_stesso_run_non_cambia_il_censimento()
    {
        var anomalo = await Seed.AddCollectiveAsync(components: 2);
        await Seed.AddCollectiveAsync(components: 2);
        await Seed.AddModelAsync(await PrimoComponenteAsync(anomalo));

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Collective, db.Options, DateTimeOffset.Now, default);
        var run = await db.Store.LoadAsync(runId, default);
        var strategy = db.Strategies.Resolve(run.Strategy);

        var prima = await strategy.SelectAsync(run, default);
        var censimentoPrima = await CensimentoAsync(runId);

        var seconda = await strategy.SelectAsync(run, default);
        var censimentoSeconda = await CensimentoAsync(runId);

        Assert.Equal(2, prima);
        Assert.Equal(0, seconda);
        Assert.Equal(censimentoPrima.OrderBy(c => c.CollectiveOrderId),
                     censimentoSeconda.OrderBy(c => c.CollectiveOrderId));
    }
}
