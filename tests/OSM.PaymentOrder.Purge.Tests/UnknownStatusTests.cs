using Microsoft.Extensions.DependencyInjection;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-15: il censimento degli stati non riconosciuti come terminali.
///
/// E' l'unica deriva dello schema che resta silenziosa. Una tabella nuova fa
/// fallire una DELETE; una colonna rimossa fa fallire una query. Uno stato
/// conclusivo aggiunto dall'applicazione e non aggiunto a TerminalStates non
/// produce nulla: quegli ordini non diventano mai eleggibili, restano a
/// database per sempre, e il purge continua a girare verde.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class UnknownStatusTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    /// <summary>
    /// Esegue il run e produce il report. La fixture gira in modalita' reale
    /// (DryRun = false), quindi al ritorno i candidati sono stati cancellati e
    /// i loro record di staging sono in stato Deleted: il censimento pero'
    /// legge PaymentOrder.Order, non lo staging, ed e' quello che conta.
    /// </summary>
    private async Task<DryRunReport> ReportAsync(RetentionStrategy strategy)
    {
        var runId = await db.Store.CreateAsync(strategy, db.Options, DateTimeOffset.Now, default);
        await db.Orchestrator.RunAsync(runId, default);
        var run = await db.Store.LoadAsync(runId, default);
        return await db.Services.GetRequiredService<DryRunReporter>().ProduceAsync(run, default);
    }

    [Fact]
    public async Task Con_soli_stati_noti_il_censimento_e_vuoto()
    {
        await Seed.AddOrderAsync(revisions: 1);

        var report = await ReportAsync(RetentionStrategy.Terminated);

        Assert.Empty(report.UnknownStatuses);
        Assert.DoesNotContain("Stati non riconosciuti", report.ToText());
        Assert.Equal(0, await Seed.CountAsync("[Order]"));
    }

    /// <summary>
    /// Uno stato conclusivo nuovo, oltre soglia: non viene cancellato — ed e'
    /// corretto, perche' nessuno ha detto al purge che e' terminale — ma
    /// compare nel report con il conteggio e la data piu' vecchia, cosi' chi
    /// legge puo' porre la domanda.
    /// </summary>
    [Fact]
    public async Task Uno_stato_sconosciuto_oltre_soglia_compare_nel_report()
    {
        var noto = await Seed.AddOrderAsync();
        await Seed.AddOrderAsync(statusCode: "Settled");

        var report = await ReportAsync(RetentionStrategy.Terminated);

        var riga = Assert.Single(report.UnknownStatuses);
        Assert.Equal("Settled", riga.StatusCode);
        Assert.Equal(1, riga.Count);
        Assert.NotNull(riga.Oldest);

        var testo = report.ToText();
        Assert.Contains("Stati non riconosciuti", testo);
        Assert.Contains("Settled", testo);

        // Il censimento commenta la selezione, non la allarga: l'ordine noto
        // e' stato cancellato, quello in stato sconosciuto e' ancora li'.
        // (La fixture esegue in modalita' reale, non simulata.)
        Assert.Equal(1, await Seed.CountAsync("[Order]"));
        Assert.Equal(1, await db.Sql.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Purge.RunCandidateOrder WHERE OrderId = @Id AND State = 'Deleted';",
            default, SqlParam.Of("@Id", noto)));
        Assert.Equal("Settled", await db.Sql.ScalarAsync<string>(
            "SELECT StatusCode FROM PaymentOrder.[Order];", default));
    }

    /// <summary>
    /// Gli stati degli ordini mai completati non sono sconosciuti: sono la
    /// popolazione della strategia Abandoned, oggi disattivata. Segnalarli a
    /// ogni dry-run sarebbe rumore che a lungo andare rende il censimento
    /// inutile.
    /// </summary>
    [Fact]
    public async Task Gli_stati_degli_abbandonati_non_sono_sconosciuti()
    {
        await Seed.AddOrderAsync(statusCode: "Created");

        var report = await ReportAsync(RetentionStrategy.Terminated);

        Assert.Empty(report.UnknownStatuses);
    }

    /// <summary>Sotto soglia non e' urgente: il censimento guarda solo cio' che sarebbe gia' eleggibile.</summary>
    [Fact]
    public async Task Uno_stato_sconosciuto_sotto_soglia_non_compare()
    {
        await Seed.AddOrderAsync(statusCode: "Settled", executionDate: DateTime.Today.AddDays(-1));

        var report = await ReportAsync(RetentionStrategy.Terminated);

        Assert.Empty(report.UnknownStatuses);
    }
}
