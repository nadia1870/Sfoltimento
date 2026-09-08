using OSM.PaymentOrder.Purge.Migrations;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Il .csproj elenca a mano gli script incorporati (D-13). E' il punto in
/// cui una migrazione nuova puo' restare fuori: il file esiste in db/, il
/// DBA lo vede, `purge migrate` no. Qui la regola e' esplicita — ogni script
/// di db/ e' una migrazione, salvo quelli esclusi per un motivo scritto — e
/// il test fallisce alla prima dimenticanza, senza database.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MigrationContractTests
{
    /// <summary>Script di db/ che NON sono migrazioni, con il motivo.</summary>
    private static readonly Dictionary<string, string> Esclusi = new(StringComparer.Ordinal)
    {
        ["000_install_purge.sql"] = "installazione autonoma con sqlcmd, contiene gia' tutto",
        ["002_indexes.sql"] = "indici sulle tabelle applicative: manuale, in finestra di manutenzione",
        ["003_preflight.sql"] = "verifica, produce result set",
        ["004_verify_fk.sql"] = "verifica, produce result set",
        ["007_verify_collective_atomicity.sql"] = "verifica, produce result set",
        ["009_verify_audit.sql"] = "verifica, produce result set",
        ["020_analisi_pre_dry_run.sql"] = "analisi, sola lettura",
        ["021_analisi_post_dry_run.sql"] = "analisi, sola lettura",
    };

    private static DirectoryInfo DbFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OSM.PaymentOrder.Purge.sln")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("Radice del repository non trovata risalendo da " + AppContext.BaseDirectory)
            : new DirectoryInfo(Path.Combine(dir.FullName, "db"));
    }

    [Fact]
    public void Ogni_script_di_db_e_incorporato_oppure_escluso_con_un_motivo()
    {
        var suDisco = DbFolder().GetFiles("*.sql").Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var incorporati = SchemaMigrator.EmbeddedScripts().ToHashSet(StringComparer.Ordinal);

        var dimenticati = suDisco.Except(incorporati).Except(Esclusi.Keys).ToList();
        Assert.True(dimenticati.Count == 0,
            "Script in db/ ne' incorporati ne' esclusi: aggiungerli al .csproj di " +
            "OSM.PaymentOrder.Purge oppure a Esclusi con il motivo. " + string.Join(", ", dimenticati));

        var fantasma = incorporati.Except(suDisco).ToList();
        Assert.True(fantasma.Count == 0, "Incorporati ma assenti in db/: " + string.Join(", ", fantasma));

        var esclusiMaIncorporati = incorporati.Intersect(Esclusi.Keys).ToList();
        Assert.True(esclusiMaIncorporati.Count == 0,
            "Esclusi per un motivo ma comunque incorporati: " + string.Join(", ", esclusiMaIncorporati));
    }

    [Fact]
    public void Gli_script_incorporati_hanno_un_numero_e_sono_in_ordine()
    {
        var incorporati = SchemaMigrator.EmbeddedScripts();

        Assert.NotEmpty(incorporati);
        Assert.All(incorporati, n => Assert.Matches(@"^\d{3}_[a-z0-9_]+\.sql$", n));
        Assert.Equal(incorporati.OrderBy(n => n, StringComparer.Ordinal), incorporati);
        Assert.Equal("001_purge_schema.sql", incorporati[0]);
    }
}
