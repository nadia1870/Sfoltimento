using System.Reflection;
using DbUp;
using DbUp.Engine;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace OSM.PaymentOrder.Purge.Migrations;

/// <summary>
/// Applica gli script di <c>db/</c> allo schema Purge, in ordine, registrando
/// in <c>Purge.SchemaVersions</c> quali sono gia' passati (D-13).
///
/// Gli script restano file SQL, gli stessi che il DBA legge e puo' eseguire
/// a mano: qui vengono solo incorporati nell'assembly e tracciati. Non e' un
/// modello EF: la vista, gli indici filtrati e le ALTER idempotenti sarebbero
/// SQL raw dentro C#, meno leggibile di com'e' ora, e un secondo contesto EF
/// sullo stesso database dell'applicazione condividerebbe la history table
/// con le migration EF 3.1 dell'app.
///
/// Cosa NON passa da qui, di proposito:
///   - 000_install_purge.sql: e' lo script autonomo per chi installa con
///     sqlcmd. Un database installato con 000 ha gia' tutto: il primo
///     <c>migrate</c> riesegue 001..0NN, che sono idempotenti, e popola il
///     journal. Nessun baseline manuale.
///   - 002_indexes.sql: tocca tabelle dell'applicazione, grandi e in uso.
///     Va applicato dal DBA in finestra di manutenzione, con ONLINE = ON.
///   - 003/004/007/009 (verifiche) e 020/021 (analisi): producono result
///     set da leggere, non modificano lo schema.
/// L'elenco e' nel .csproj; MigrationContractTests verifica che nessun
/// nuovo script di migrazione resti fuori per dimenticanza.
///
/// SchemaVerifier resta la rete di sicurezza all'avvio: dice se il database
/// e' allineato, non come allinearlo. Questo dice come.
/// </summary>
public sealed class SchemaMigrator(
    string connectionString,
    ILogger<SchemaMigrator> log,
    int commandTimeoutSeconds = 300)
{
    public const string JournalSchema = "Purge";
    public const string JournalTable = "SchemaVersions";

    /// <summary>
    /// Prefisso delle risorse incorporate: il Link nel .csproj mette gli
    /// script sotto <c>db/</c>, quindi il nome risorsa e'
    /// <c>OSM.PaymentOrder.Purge.db.001_purge_schema.sql</c>. DbUp ordina per
    /// nome, e i tre numeri iniziali fanno l'ordine.
    /// </summary>
    internal const string ResourcePrefix = "OSM.PaymentOrder.Purge.db.";

    /// <summary>Nomi degli script incorporati, in ordine di applicazione.</summary>
    public static IReadOnlyList<string> EmbeddedScripts() =>
        typeof(SchemaMigrator).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                     && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Stessa guardia di 000_install_purge.sql: uno script che crea uno
    /// schema su un database sbagliato e' un errore che si scopre tardi.
    /// La fixture dei test la disattiva perche' i suoi database si chiamano
    /// PurgeTests_*.
    /// </summary>
    public string? RequireDatabaseNameContains { get; init; } = "PaymentOrder";

    public sealed record MigrationStatus(
        IReadOnlyList<string> Applied,
        IReadOnlyList<string> Pending);

    /// <summary>
    /// DbUp identifica gli script con il nome completo della risorsa, ed e'
    /// quello che finisce nel journal. Verso l'esterno si espone il nome del
    /// file, che e' quello che compare in db/ e nella documentazione.
    /// </summary>
    private static string Short(string resourceName) =>
        resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            ? resourceName[ResourcePrefix.Length..]
            : resourceName;

    public MigrationStatus Status()
    {
        var engine = Build();
        var pending = engine.GetScriptsToExecute().Select(s => Short(s.Name)).ToList();
        var applied = engine.GetExecutedScripts().Select(Short).ToList();
        return new MigrationStatus(applied, pending);
    }

    /// <summary>
    /// Applica gli script mancanti. Ogni script in una transazione propria:
    /// se il quinto fallisce, i primi quattro restano registrati e il quinto
    /// non lascia mezzo lavoro. Rilancia se qualcosa fallisce: un
    /// allineamento parziale non deve mai sembrare riuscito.
    /// </summary>
    public IReadOnlyList<string> Migrate()
    {
        GuardDatabaseName();

        var engine = Build();
        var pending = engine.GetScriptsToExecute();

        if (pending.Count == 0)
        {
            log.LogInformation("PurgeSchemaUpToDate Journal={Schema}.{Table}", JournalSchema, JournalTable);
            return [];
        }

        log.LogInformation("PurgeSchemaMigrating Script={Count} Primo={First} Ultimo={Last}",
            pending.Count, Short(pending[0].Name), Short(pending[^1].Name));

        var result = engine.PerformUpgrade();

        if (!result.Successful)
        {
            var fallito = result.ErrorScript is null ? "?" : Short(result.ErrorScript.Name);
            log.LogError(result.Error, "PurgeSchemaMigrationFailed Script={Script}", fallito);
            throw new InvalidOperationException(
                $"Migrazione fallita sullo script {fallito}: {result.Error?.Message}", result.Error);
        }

        var applied = result.Scripts.Select(s => Short(s.Name)).ToList();
        log.LogInformation("PurgeSchemaMigrated Applicati={Count}", applied.Count);
        return applied;
    }

    /// <summary>
    /// Lo schema va indicato a DbUp, non lasciato a 001.
    ///
    /// DbUp crea la tabella del journal prima di eseguire ogni script, quindi
    /// alla prima esecuzione tenterebbe di creare Purge.SchemaVersions su un
    /// database in cui lo schema Purge non esiste ancora — lo creerebbe 001,
    /// che a quel punto non e' ancora partito. Con il nome dello schema nel
    /// costruttore DbUp lo crea per primo, e la CREATE SCHEMA idempotente di
    /// 001 non trova niente da fare.
    /// </summary>
    private UpgradeEngine Build() =>
        DeployChanges.To
            .SqlDatabase(connectionString, JournalSchema)
            .WithScriptsEmbeddedInAssembly(
                typeof(SchemaMigrator).Assembly,
                n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                  && n.EndsWith(".sql", StringComparison.Ordinal))
            .JournalToSqlTable(JournalSchema, JournalTable)
            .WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromSeconds(commandTimeoutSeconds))
            .LogTo(new DbUpLogAdapter(log))
            .Build();

    private void GuardDatabaseName()
    {
        if (string.IsNullOrEmpty(RequireDatabaseNameContains)) return;

        var db = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!db.Contains(RequireDatabaseNameContains, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Database '{db}': il nome non contiene '{RequireDatabaseNameContains}'. " +
                "Verificare di essere sul database giusto prima di creare lo schema Purge.");
        }
    }
}
