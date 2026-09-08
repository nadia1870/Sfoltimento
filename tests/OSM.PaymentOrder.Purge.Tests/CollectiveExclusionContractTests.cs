using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Vincoli statici delle esclusioni (D-10), verificabili senza database.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CollectiveExclusionContractTests
{
    /// <summary>
    /// ExcludedReason e' VARCHAR(60). Il motivo per il caso C7 porta il nome
    /// della tabella di storico: una tabella con un nome lungo aggiunta alla
    /// topologia troncherebbe il motivo — o fallirebbe l'UPDATE con un
    /// errore di troncamento, a meta' della selezione.
    /// </summary>
    [Fact]
    public void I_motivi_di_esclusione_stanno_nella_colonna()
    {
        const int excludedReasonLength = 60;

        foreach (var (reason, _) in CollectiveStrategy.ExclusionStatements())
            Assert.True(reason.Length <= excludedReasonLength,
                $"'{reason}' supera i {excludedReasonLength} caratteri di ExcludedReason.");
    }

    /// <summary>Una tabella di storico aggiunta alla topologia genera da sola la propria esclusione.</summary>
    [Fact]
    public void Le_esclusioni_seguono_la_topologia()
    {
        var reasons = CollectiveStrategy.ExclusionStatements().Select(e => e.Reason).ToList();

        Assert.Equal(2 + PurgeTopology.DetailHistoryTables.Count, reasons.Count);
        Assert.Equal(reasons.Count, reasons.Distinct().Count());

        foreach (var t in PurgeTopology.DetailHistoryTables)
            Assert.Contains(RetentionSql.CrossReferenceReasonPrefix + t.Name, reasons);
    }

    /// <summary>
    /// Ogni esclusione tocca solo i collettivi ancora 'Selected' e li porta in
    /// 'Excluded': e' cio' che le rende idempotenti alla riesecuzione.
    /// </summary>
    [Fact]
    public void Le_esclusioni_sono_idempotenti_per_costruzione()
    {
        foreach (var (reason, sql) in CollectiveStrategy.ExclusionStatements())
        {
            Assert.Contains("rc.State = 'Selected'", sql);
            Assert.Contains("SET State = 'Excluded'", sql);
            Assert.Contains($"ExcludedReason = '{reason}'", sql);
        }
    }
}
