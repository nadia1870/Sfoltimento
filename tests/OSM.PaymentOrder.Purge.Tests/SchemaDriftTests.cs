using Microsoft.Extensions.Logging.Abstractions;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-15: una tabella che referenzia Order o OrderHistory senza essere in
/// PurgeTopology.
///
/// E' la deriva piu' pericolosa fra quelle possibili, perche' non nasce da un
/// errore nel purge: nasce da un'applicazione che aggiunge una tabella e non
/// sa che il purge esiste. Nessuna DELETE la tocca, la foreign key blocca la
/// cancellazione della testata, e prima di questo controllo il sintomo
/// arrivava di notte come una sequenza di aggregati abbandonati con errore
/// 547 — uno per uno, dopo la bisezione.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class SchemaDriftTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public async Task DisposeAsync()
    {
        await db.Sql.ExecuteAsync(
            "IF OBJECT_ID('PaymentOrder.TabellaNuova') IS NOT NULL DROP TABLE PaymentOrder.TabellaNuova;" +
            "IF OBJECT_ID('PaymentOrder.StoricoNuovo') IS NOT NULL DROP TABLE PaymentOrder.StoricoNuovo;",
            default);
    }

    private SchemaVerifier Sut => new(db.Sql, NullLogger<SchemaVerifier>.Instance);

    [Fact]
    public async Task Uno_schema_allineato_non_riporta_tabelle_inattese()
    {
        Assert.Empty(await Sut.FindUnexpectedChildrenAsync(default));
        await Sut.EnsureAsync(default);
    }

    /// <summary>
    /// Il caso reale: l'applicazione aggiunge una tabella legata a Order.
    /// Il motore deve rifiutarsi di partire, e il messaggio deve nominare la
    /// tabella — la diagnosi per tentativi su un 547 notturno era il costo
    /// che questo controllo elimina.
    /// </summary>
    [Fact]
    public async Task Una_tabella_nuova_legata_a_Order_impedisce_l_avvio()
    {
        await db.Sql.ExecuteAsync("""
            CREATE TABLE PaymentOrder.TabellaNuova (
                Id      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                OrderId UNIQUEIDENTIFIER NOT NULL
                        CONSTRAINT FK_TabellaNuova_Order REFERENCES PaymentOrder.[Order](Id)
            );
            """, default);

        var inattese = await Sut.FindUnexpectedChildrenAsync(default);

        Assert.Equal("PaymentOrder.TabellaNuova -> Order", Assert.Single(inattese));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Sut.EnsureAsync(default));
        Assert.Contains("TabellaNuova", ex.Message);
    }

    [Fact]
    public async Task Una_tabella_nuova_legata_a_OrderHistory_viene_rilevata()
    {
        await db.Sql.ExecuteAsync("""
            CREATE TABLE PaymentOrder.StoricoNuovo (
                Id             UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                OrderHistoryId UNIQUEIDENTIFIER NOT NULL
                               CONSTRAINT FK_StoricoNuovo_OrderHistory
                               REFERENCES PaymentOrder.OrderHistory(Id)
            );
            """, default);

        Assert.Equal("PaymentOrder.StoricoNuovo -> OrderHistory",
            Assert.Single(await Sut.FindUnexpectedChildrenAsync(default)));
    }

    /// <summary>
    /// Una tabella che referenzia un dettaglio, non la testata, non e'
    /// coperta: sarebbe un grafo diverso da quello che la topologia descrive,
    /// e va affrontata li'. Il test fissa il limite dichiarato, cosi' chi
    /// legge non lo scambia per una dimenticanza.
    /// </summary>
    [Fact]
    public async Task Una_tabella_legata_a_un_dettaglio_non_viene_rilevata()
    {
        await db.Sql.ExecuteAsync("""
            CREATE TABLE PaymentOrder.TabellaNuova (
                Id             UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                BankTransferId UNIQUEIDENTIFIER NOT NULL
                               CONSTRAINT FK_TabellaNuova_BankTransfer
                               REFERENCES PaymentOrder.BankTransfer(Id)
            );
            """, default);

        Assert.Empty(await Sut.FindUnexpectedChildrenAsync(default));
    }
}

/// <summary>Vincoli statici della lista dei figli attesi, senza database.</summary>
[Trait("Category", "Unit")]
public sealed class ExpectedChildrenContractTests
{
    /// <summary>
    /// I figli attesi derivano dai gruppi di cancellazione: una tabella
    /// aggiunta alla topologia compare qui da sola, e non serve ricordarsi di
    /// aggiornare una seconda lista.
    /// </summary>
    [Fact]
    public void I_figli_attesi_derivano_dalla_topologia()
    {
        var attesi = PurgeTopology.ExpectedChildren().ToDictionary(e => e.Parent, e => e.Children);

        Assert.Equal(["Order", "OrderHistory"], attesi.Keys.OrderBy(k => k));

        foreach (var t in PurgeTopology.DetailTables)
            Assert.Contains(t, attesi["Order"]);

        Assert.Contains("OrderHistory", attesi["Order"]);

        foreach (var t in PurgeTopology.DetailHistoryTables)
            Assert.Contains(t.Name, attesi["OrderHistory"]);
    }

    /// <summary>Il confronto con i nomi del database non deve dipendere dalle maiuscole.</summary>
    [Fact]
    public void Il_confronto_ignora_le_maiuscole()
    {
        var attesi = PurgeTopology.ExpectedChildren().ToDictionary(e => e.Parent, e => e.Children);

        Assert.Contains("banktransfer", attesi["Order"]);
        Assert.Contains("BANKTRANSFERHISTORY", attesi["OrderHistory"]);
    }
}
