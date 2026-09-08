using Microsoft.Extensions.Logging.Abstractions;
using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// 000_install_purge.sql si dichiara autonomo, e per un periodo non lo e'
/// stato: le migrazioni 005, 011 e 012 vi mancavano, e chi seguiva il README
/// alla lettera trovava SchemaVerifier che rifiutava di partire. Il test
/// verde sulla sessione non se ne accorgeva, perche' la fixture applica le
/// migrazioni una per una.
///
/// Qui il contratto e' esplicito: tutto cio' che SchemaVerifier si aspetta
/// deve comparire nello script, per nome. E' un controllo sul testo, non sul
/// database: gira senza SQL Server e fallisce alla prima migrazione
/// dimenticata.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InstallScriptContractTests
{
    private static string Script =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "000_install_purge.sql"));

    [Fact]
    public void Lo_script_di_installazione_crea_ogni_tabella_attesa()
    {
        var script = Script;

        foreach (var table in SchemaVerifier.ExpectedTables()
                     .Where(t => t.StartsWith("Purge.", StringComparison.Ordinal))
                     .Distinct())
        {
            Assert.True(
                script.Contains($"OBJECT_ID('{table}')", StringComparison.OrdinalIgnoreCase),
                $"{table} non compare in 000_install_purge.sql.");
        }
    }

    [Fact]
    public void Lo_script_di_installazione_contiene_ogni_colonna_attesa()
    {
        var script = Script;

        foreach (var (table, column, migration) in SchemaVerifier.ExpectedColumns)
        {
            Assert.True(
                script.Contains($"COL_LENGTH('{table}', '{column}')", StringComparison.OrdinalIgnoreCase),
                $"{table}.{column} ({migration}) non e' allineata in 000_install_purge.sql: " +
                "aggiungerla alla CREATE TABLE e alla sezione ALLINEAMENTO.");
        }
    }
}

/// <summary>
/// La prova sul database vero: un database vuoto, lo schema applicativo di
/// prova e il solo 000, poi SchemaVerifier. E' esattamente il percorso del
/// README, e deve passare.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class InstallScriptTests(PurgeDatabaseFixture db)
{
    [Fact]
    public Task Il_solo_000_soddisfa_SchemaVerifier() =>
        db.OnFreshDatabaseAsync(
            ["010_test_schema.sql", "000_install_purge.sql"],
            async sql =>
            {
                var verifier = new SchemaVerifier(sql, NullLogger<SchemaVerifier>.Instance);
                await verifier.EnsureAsync(default);
            });

    /// <summary>Rieseguirlo su un database gia' installato non deve fallire ne' cambiare nulla.</summary>
    [Fact]
    public Task Lo_script_e_idempotente() =>
        db.OnFreshDatabaseAsync(
            ["010_test_schema.sql", "000_install_purge.sql", "000_install_purge.sql"],
            async sql =>
            {
                var verifier = new SchemaVerifier(sql, NullLogger<SchemaVerifier>.Instance);
                await verifier.EnsureAsync(default);
            });
}
