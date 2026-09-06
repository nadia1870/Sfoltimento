using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Confronta PurgeTopology con le tabelle realmente presenti nel database.
///
/// Senza questo controllo una tabella mancante emerge a meta' della fase di
/// validazione, come errore 208 dentro uno stack SqlClient, dopo che il run e'
/// gia' stato creato e la selezione eseguita. Il messaggio non dice quali altre
/// tabelle manchino, quindi la diagnosi procede per tentativi.
///
/// Va invocato all'avvio: una divergenza fra il modello e il database non e'
/// una condizione da gestire, e' una condizione da segnalare prima di iniziare.
/// </summary>
public sealed class SchemaVerifier(SqlExecutor sql, ILogger<SchemaVerifier> log)
{
    private const string Query = """
        SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name IN ('PaymentOrder', 'Purge');
        """;

    /// <summary>Tabelle attese: aggregato Order, coda collettiva, controllo del purge.</summary>
    public static IEnumerable<string> ExpectedTables()
    {
        foreach (var t in PurgeTopology.DetailHistoryTables)
            yield return $"PaymentOrder.{t.Name}";

        foreach (var t in PurgeTopology.DetailTables)
            yield return $"PaymentOrder.{t}";

        yield return "PaymentOrder.Order";
        yield return "PaymentOrder.OrderHistory";

        // I due nomi "Residue" della coda collettiva sono etichette di statement,
        // non tabelle: la tabella reale e' quella senza il suffisso.
        foreach (var t in PurgeTopology.CollectiveAggregateTables)
            yield return $"PaymentOrder.{t.Replace("Residue", string.Empty)}";

        foreach (var t in new[]
        {
            "PurgeRun", "RunCandidateOrder", "RunCandidateOrderHistory",
            "RunCandidateCollective", "RunBatchProgress", "ValidationFinding",
            "DryRunReport", "PurgeAudit"
        })
            yield return $"Purge.{t}";
    }

    /// <summary>
    /// Colonne aggiunte da una migrazione successiva alla creazione della
    /// tabella. La tabella esiste anche senza, quindi il controllo per nome
    /// tabella non le vedrebbe: mancherebbero al primo INSERT, a meta' della
    /// prima slice, con la transazione gia' aperta sulle tabelle di dominio.
    /// </summary>
    private static readonly (string Table, string Column, string Script)[] ExpectedColumns =
    [
        ("Purge.PurgeRun", "StagingPurgedOn", "005_housekeeping.sql"),
        ("Purge.RunCandidateOrder", "CollectiveOrderId", "006_collective_atomicity.sql"),
        ("Purge.RunCandidateCollective", "BatchNo", "006_collective_atomicity.sql"),
        ("Purge.PurgeAudit", "BatchNo", "008_audit_trail.sql"),
    ];

    private const string ColumnQuery = """
        SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + '.' + QUOTENAME(c.name)
        FROM sys.columns c
        JOIN sys.tables t  ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'Purge';
        """;

    private async Task<IReadOnlyList<string>> FindMissingColumnsAsync(CancellationToken ct)
    {
        var present = (await sql.QueryAsync(ColumnQuery, r => r.GetString(0), ct).ConfigureAwait(false))
            .Select(n => n.Replace("[", string.Empty).Replace("]", string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ExpectedColumns
            .Where(c => !present.Contains($"{c.Table}.{c.Column}"))
            .Select(c => $"{c.Table}.{c.Column} (eseguire {c.Script})")
            .ToList();
    }

    /// <summary>Restituisce le tabelle attese e assenti. Vuoto = schema allineato.</summary>
    public async Task<IReadOnlyList<string>> FindMissingAsync(CancellationToken ct)
    {
        var present = (await sql.QueryAsync(Query, r => r.GetString(0), ct).ConfigureAwait(false))
            .Select(n => n.Replace("[", string.Empty).Replace("]", string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = ExpectedTables().Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !present.Contains(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            log.LogInformation("Verifica schema superata: {Count} tabelle attese, tutte presenti.",
                ExpectedTables().Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        else
        {
            log.LogError(
                "Verifica schema fallita: {Count} tabelle attese ma assenti.{NewLine}  {List}",
                missing.Count, Environment.NewLine, string.Join(Environment.NewLine + "  ", missing));
        }

        return missing;
    }

    /// <summary>Come sopra, ma interrompe l'avvio se lo schema non e' allineato.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        var missing = await FindMissingAsync(ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Lo schema del database non corrisponde alla topologia attesa. " +
                $"Tabelle assenti: {string.Join(", ", missing)}. " +
                "Verificare di essere sul database corretto e che le migrazioni " +
                "applicative siano allineate.");
        }

        var missingColumns = await FindMissingColumnsAsync(ct).ConfigureAwait(false);
        if (missingColumns.Count > 0)
        {
            log.LogError(
                "Verifica schema fallita: {Count} colonne attese ma assenti.{NewLine}  {List}",
                missingColumns.Count, Environment.NewLine,
                string.Join(Environment.NewLine + "  ", missingColumns));

            throw new InvalidOperationException(
                "Lo schema Purge e' incompleto: alcune migrazioni non sono state applicate. " +
                $"Colonne assenti: {string.Join(", ", missingColumns)}.");
        }
    }
}
