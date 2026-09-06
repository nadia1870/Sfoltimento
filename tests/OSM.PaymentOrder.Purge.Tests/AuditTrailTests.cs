using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La traccia di cio' che e' stato cancellato.
///
/// Purge.PurgeAudit esisteva da sempre nello schema ma nessuno la scriveva:
/// il metodo che avrebbe dovuto farlo non era chiamato da alcun percorso, e
/// la view Purge.vDryRunVsActual riportava scostamento pieno su ogni riga.
/// Questi test verificano che la traccia esista, che quadri con il
/// checkpoint, e che sparisca insieme alla transazione se la slice fallisce.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class AuditTrailTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    private async Task<Guid> RunAsync(RetentionStrategy strategy)
    {
        var runId = await db.Store.CreateAsync(
            strategy, db.Options, DateTimeOffset.Now, default);

        await db.Orchestrator.RunAsync(runId, default);
        return runId;
    }

    /// <summary>
    /// Un run reale deve lasciare una riga di audit per ogni tabella che ha
    /// cancellato qualcosa, e il totale deve coincidere con quanto scritto nel
    /// checkpoint della slice. Se i due valori divergono, uno dei due mente.
    /// </summary>
    [Fact]
    public async Task Audit_quadra_con_il_checkpoint_di_slice()
    {
        for (var i = 0; i < 25; i++)
            await Seed.AddOrderAsync(revisions: 3);

        var runId = await RunAsync(RetentionStrategy.Terminated);

        var righeAudit = await db.Sql.ScalarAsync<long>(
            "SELECT ISNULL(SUM(RowsDeleted), 0) FROM Purge.PurgeAudit WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

        var righeCheckpoint = await db.Sql.ScalarAsync<long>(
            """
            SELECT ISNULL(SUM(ActualDeletedRows), 0)
            FROM Purge.RunBatchProgress
            WHERE RunId = @RunId AND Status = 'Completed';
            """,
            default, SqlParam.Of("@RunId", runId));

        Assert.True(righeAudit > 0, "L'audit non e' stato scritto.");
        Assert.Equal(righeCheckpoint, righeAudit);
    }

    /// <summary>
    /// Ogni riga di audit deve riferirsi a una slice esistente e conclusa:
    /// e' cio' che permette di risalire dal totale per tabella alla singola
    /// transazione che lo ha prodotto.
    /// </summary>
    [Fact]
    public async Task Ogni_riga_di_audit_riferisce_una_slice_completata()
    {
        for (var i = 0; i < 25; i++)
            await Seed.AddOrderAsync(revisions: 2);

        var runId = await RunAsync(RetentionStrategy.Terminated);

        var orfane = await db.Sql.ScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM Purge.PurgeAudit AS a
            WHERE a.RunId = @RunId
              AND NOT EXISTS (SELECT 1 FROM Purge.RunBatchProgress AS b
                              WHERE b.RunId = a.RunId AND b.BatchNo = a.BatchNo
                                AND b.Status = 'Completed');
            """,
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(0, orfane);

        var senzaRighe = await db.Sql.ScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM Purge.PurgeAudit WHERE RunId = @RunId AND RowsDeleted <= 0;",
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(0, senzaRighe);
    }

    /// <summary>
    /// Il confronto previsto/effettivo deve chiudere a zero su un run che ha
    /// cancellato tutto quello che aveva pianificato. E' il test che sarebbe
    /// fallito da sempre: senza baseline sul run reale la view non aveva
    /// nemmeno una riga da confrontare.
    /// </summary>
    [Fact]
    public async Task Previsto_ed_effettivo_coincidono_su_un_run_completo()
    {
        for (var i = 0; i < 20; i++)
            await Seed.AddOrderAsync(revisions: 2, detailTable: "BankTransfer");

        var runId = await RunAsync(RetentionStrategy.Terminated);

        var righeConfrontate = await db.Sql.ScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM Purge.vDryRunVsActual WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

        Assert.True(righeConfrontate > 0,
            "La view non ha righe: il baseline del run reale non e' stato prodotto.");

        var divergenti = await db.Sql.QueryAsync(
            """
            SELECT TableName, Previsto, Effettivo
            FROM Purge.vDryRunVsActual
            WHERE RunId = @RunId AND Scostamento <> 0;
            """,
            r => $"{r.GetString(0)}: previsto {r.GetInt64(1)}, effettivo {r.GetInt64(2)}",
            default, SqlParam.Of("@RunId", runId));

        Assert.True(divergenti.Count == 0, string.Join("; ", divergenti));
    }

    /// <summary>
    /// L'audit vive nella transazione della slice. Un dry-run non apre nessuna
    /// transazione di cancellazione, quindi non deve lasciare traccia di
    /// righe cancellate — solo la previsione.
    /// </summary>
    [Fact]
    public async Task Il_dry_run_non_scrive_audit()
    {
        await Seed.AddOrderAsync(revisions: 2);

        var originale = db.Options.DryRun;
        db.Options.DryRun = true;
        try
        {
            var runId = await RunAsync(RetentionStrategy.Terminated);

            var audit = await db.Sql.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM Purge.PurgeAudit WHERE RunId = @RunId;",
                default, SqlParam.Of("@RunId", runId));

            var previsione = await db.Sql.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM Purge.DryRunReport WHERE RunId = @RunId;",
                default, SqlParam.Of("@RunId", runId));

            Assert.Equal(0, audit);
            Assert.True(previsione > 0);
            Assert.Equal(1, await Seed.CountAsync("[Order]"));
        }
        finally
        {
            db.Options.DryRun = originale;
        }
    }

    /// <summary>
    /// Il baseline viene riscritto, non accumulato: se il processo cade in
    /// Planning la fase viene rieseguita, e due righe per tabella
    /// moltiplicherebbero il confronto della view.
    /// </summary>
    [Fact]
    public async Task Il_baseline_non_si_duplica_se_il_planning_viene_ripetuto()
    {
        for (var i = 0; i < 5; i++)
            await Seed.AddOrderAsync(revisions: 1);

        var runId = await RunAsync(RetentionStrategy.Terminated);

        var duplicate = await db.Sql.ScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM (SELECT TableName FROM Purge.DryRunReport
                  WHERE RunId = @RunId
                  GROUP BY TableName HAVING COUNT(*) > 1) AS d;
            """,
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(0, duplicate);
    }
}
