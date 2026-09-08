using Microsoft.Extensions.Logging.Abstractions;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Migrations;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// `purge migrate` sul database vero (D-13): da vuoto, da un'installazione
/// 000, e due volte di seguito.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class MigrationTests(PurgeDatabaseFixture db)
{
    private static Task<int> JournalCountAsync(SqlExecutor sql) =>
        sql.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {SchemaMigrator.JournalSchema}.{SchemaMigrator.JournalTable};", default);

    private static Task VerifyAsync(SqlExecutor sql) =>
        new SchemaVerifier(sql, NullLogger<SchemaVerifier>.Instance).EnsureAsync(default);

    [Fact]
    public Task Da_un_database_vuoto_la_migrazione_soddisfa_SchemaVerifier() =>
        db.OnFreshDatabaseAsync(["010_test_schema.sql"], async sql =>
        {
            var migrator = PurgeDatabaseFixture.Migrator(sql.ConnectionString);

            var prima = migrator.Status();
            Assert.Empty(prima.Applied);
            Assert.Equal(SchemaMigrator.EmbeddedScripts(), prima.Pending);

            var applicati = migrator.Migrate();

            Assert.Equal(SchemaMigrator.EmbeddedScripts(), applicati);
            Assert.Equal(applicati.Count, await JournalCountAsync(sql));
            await VerifyAsync(sql);

            var dopo = migrator.Status();
            Assert.Empty(dopo.Pending);
            Assert.Equal(SchemaMigrator.EmbeddedScripts(), dopo.Applied);
        });

    [Fact]
    public Task Una_seconda_migrazione_non_fa_nulla() =>
        db.OnFreshDatabaseAsync(["010_test_schema.sql"], async sql =>
        {
            var migrator = PurgeDatabaseFixture.Migrator(sql.ConnectionString);
            migrator.Migrate();
            var journal = await JournalCountAsync(sql);

            var seconda = migrator.Migrate();

            Assert.Empty(seconda);
            Assert.Equal(journal, await JournalCountAsync(sql));
        });

    /// <summary>
    /// Un database installato con 000 ha gia' tutto ma nessun journal. Il
    /// primo migrate riesegue gli script — idempotenti — e popola il journal
    /// senza errori. Non serve un baseline manuale, ed e' il percorso di chi
    /// ha installato a mano prima che esistesse `purge migrate`.
    /// </summary>
    [Fact]
    public Task Su_un_database_installato_con_000_la_migrazione_passa_e_popola_il_journal() =>
        db.OnFreshDatabaseAsync(["010_test_schema.sql", "000_install_purge.sql"], async sql =>
        {
            var migrator = PurgeDatabaseFixture.Migrator(sql.ConnectionString);

            var applicati = migrator.Migrate();

            Assert.Equal(SchemaMigrator.EmbeddedScripts(), applicati);
            Assert.Equal(applicati.Count, await JournalCountAsync(sql));
            await VerifyAsync(sql);
        });

    /// <summary>La guardia sul nome del database vale anche qui, come in 000.</summary>
    [Fact]
    public Task La_guardia_sul_nome_del_database_rifiuta_un_nome_estraneo() =>
        db.OnFreshDatabaseAsync(["010_test_schema.sql"], async sql =>
        {
            var migrator = new SchemaMigrator(sql.ConnectionString, NullLogger<SchemaMigrator>.Instance)
            {
                RequireDatabaseNameContains = "NomeCheNonCompare"
            };

            var ex = Assert.Throws<InvalidOperationException>(() => migrator.Migrate());
            Assert.Contains("NomeCheNonCompare", ex.Message);

            // E non ha creato niente.
            Assert.Null(await sql.ScalarAsync<int?>("SELECT OBJECT_ID('Purge.PurgeRun');", default));
        });
}
