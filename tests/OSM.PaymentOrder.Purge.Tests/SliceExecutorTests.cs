using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Observability;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La macchina della slice, provata senza database.
///
/// Erano gli invarianti piu' importanti del motore e i meno verificabili: si
/// osservavano solo attraverso un run completo, dove un esito sbagliato si
/// confonde con dieci altre cause. Con IPurgeSession si puo' far fallire
/// l'ennesimo statement e guardare cosa succede al resto.
///
/// Trasversale a tutti i casi non riusciti: CommitAsync non deve essere
/// chiamato nemmeno una volta.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SliceExecutorTests
{
    private const string StatementOrder = "DELETE_ORDER";
    private const string StatementDettaglio = "DELETE_DETTAGLIO";
    private const string StatementStorico = "DELETE_STORICO";

    /// <summary>
    /// Tre statement con testo riconoscibile, in ordine. Usare la strategia
    /// vera legherebbe questi test alla topologia FK, che e' provata altrove.
    /// </summary>
    private sealed class StrategiaFinta : IPurgeStrategy
    {
        public RetentionStrategy Type => RetentionStrategy.Terminated;
        public PurgePlanningMode PlanningMode => PurgePlanningMode.Standard;
        public bool SkipCollectiveLinkValidation => false;
        public bool UsesAbandonedDeletes => false;

        public DateTime CutoffOf(PurgeRun run) => run.RetentionCutoff;
        public Task<int> SelectAsync(PurgeRun run, CancellationToken ct) => Task.FromResult(0);
        public Task ExpandAsync(PurgeRun run, CancellationToken ct) => Task.CompletedTask;

        public IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
        [
            ("OrderHistory", StatementStorico),
            ("BankTransfer", StatementDettaglio),
            ("Order",        StatementOrder),
        ];
    }

    private static SliceExecutor Sut(FakePurgeSession session, Exception? openFailure = null)
    {
        var metrics = new ServiceCollection()
            .AddMetrics().AddSingleton<PurgeMetrics>()
            .BuildServiceProvider().GetRequiredService<PurgeMetrics>();

        return new SliceExecutor(
            new FakeSqlExecutor(session, openFailure),
            new PurgeStrategyResolver([new StrategiaFinta()]),
            metrics,
            NullLogger<SliceExecutor>.Instance);
    }

    private static PurgeRun Run() => new()
    {
        RunId = Guid.NewGuid(),
        Strategy = RetentionStrategy.Terminated,
        Phase = RunPhase.Executing,
        DryRun = false,
        AnchorMode = RetentionAnchorMode.RollingDate,
        RetentionCutoff = DateTime.UtcNow,
        MaxRowsPerBatch = 3000,
        MaxOrdersPerBatch = 500
    };

    private static SliceInfo Slice(int orderCount = 1) => new()
    {
        BatchNo = 0,
        OrderCount = orderCount,
        EstimatedRowCount = 3,
        AttemptCount = 0,
        IsOversized = false
    };

    // ------------------------------------------------------------- successo

    /// <summary>
    /// Il percorso riuscito: tutti gli statement, poi l'audit, poi il
    /// checkpoint, poi un solo commit. L'ordine conta quanto il risultato.
    /// </summary>
    [Fact]
    public async Task Percorso_riuscito_scrive_audit_checkpoint_e_committa_una_volta()
    {
        var session = new FakePurgeSession { DefaultRowCount = 1 };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None);

        Assert.Equal(SliceOutcome.Completed, result.Outcome);
        Assert.Equal(1, session.Commits);
        Assert.Equal(0, session.Rollbacks);
        Assert.True(session.Disposed);

        var posizioneAudit = session.Executed.FindIndex(s => s.Contains("PurgeAudit"));
        var posizioneCheckpoint = session.Executed.FindIndex(s => s.Contains("RunBatchProgress"));
        var posizioneOrder = session.Executed.IndexOf(StatementOrder);

        Assert.True(posizioneOrder < posizioneAudit, "l'audit deve seguire le cancellazioni");
        Assert.True(posizioneAudit < posizioneCheckpoint, "il checkpoint deve chiudere");
    }

    /// <summary>
    /// Le tabelle a zero righe non entrano nell'audit: sono la maggioranza in
    /// ogni slice e renderebbero la traccia illeggibile.
    /// </summary>
    [Fact]
    public async Task L_audit_ignora_le_tabelle_a_zero_righe()
    {
        var session = new FakePurgeSession
        {
            DefaultRowCount = 0,
            RowCountByFragment = { [StatementOrder] = 1 }
        };

        await Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None);

        var audit = session.Executed.Single(s => s.Contains("PurgeAudit"));

        // Una riga sola: solo Order ha cancellato qualcosa.
        Assert.Contains("@T0", audit);
        Assert.DoesNotContain("@T1", audit);
    }

    // ------------------------------------------------- classificazione D-7

    /// <summary>
    /// Deadlock a meta' sequenza: rollback, esito riprovabile, e soprattutto
    /// lo statement successivo non viene eseguito.
    /// </summary>
    [Fact]
    public async Task Un_deadlock_annulla_e_rende_la_slice_riprovabile()
    {
        var session = new FakePurgeSession
        {
            FailAtStatement = 2,
            Failure = await SqlExceptionFactory.WithNumberAsync(1205)
        };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None);

        Assert.Equal(SliceOutcome.Retryable, result.Outcome);
        Assert.Equal(1, session.Rollbacks);
        Assert.Equal(0, session.Commits);
        Assert.Equal(0, session.CountContaining(StatementOrder));
    }

    /// <summary>
    /// Connessione caduta: rilancia invece di restituire un esito. E' D-7
    /// provata invece che dichiarata — se qui tornasse Retryable o Fatal, il
    /// coordinatore abbandonerebbe la slice e gli aggregati resterebbero a
    /// database per un guasto passeggero.
    /// </summary>
    [Fact]
    public async Task Un_guasto_di_connessione_risale_invece_di_abbandonare()
    {
        var session = new FakePurgeSession
        {
            FailAtStatement = 2,
            Failure = await SqlExceptionFactory.WithNumberAsync(4060)
        };

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None));

        Assert.Equal(1, session.Rollbacks);
        Assert.Equal(0, session.Commits);
        Assert.True(session.Disposed);
    }

    /// <summary>Un errore non classificato abbandona la slice, e non e' divisibile.</summary>
    [Fact]
    public async Task Un_errore_non_classificato_e_fatale()
    {
        var session = new FakePurgeSession
        {
            FailAtStatement = 1,
            Failure = new InvalidOperationException("vincolo violato")
        };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None);

        Assert.Equal(SliceOutcome.Fatal, result.Outcome);
        Assert.False(result.Splittable);
        Assert.Equal(1, session.Rollbacks);
        Assert.Equal(0, session.Commits);
    }

    /// <summary>
    /// D-11: una FK violata e' fatale per questa slice, ma e' un dato che
    /// rifiuta la cancellazione e non un difetto. L'esito lo dichiara
    /// divisibile, cosi' il coordinatore puo' isolare l'aggregato invece di
    /// abbandonare tutti quelli che gli stanno accanto. Lo statement
    /// successivo non parte e il commit non avviene.
    /// </summary>
    [Fact]
    public async Task Una_fk_violata_e_fatale_ma_divisibile()
    {
        var session = new FakePurgeSession
        {
            FailAtStatement = 3,
            Failure = SqlExceptionFactory.Create(547, "FK_OrderHistory_Order violata")
        };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(orderCount: 5), CancellationToken.None);

        Assert.Equal(SliceOutcome.Fatal, result.Outcome);
        Assert.True(result.Splittable);
        Assert.Equal(1, session.Rollbacks);
        Assert.Equal(0, session.Commits);
        Assert.Equal(3, session.Executed.Count);
        Assert.DoesNotContain(session.Executed, s => s.Contains("PurgeAudit"));
    }

    /// <summary>
    /// Una SqlException con un numero che non e' ne' transitorio ne' di
    /// integrita' — un difetto, tipo un oggetto inesistente — resta un
    /// abbandono in blocco: dividerla fallirebbe ogni figlia allo stesso modo.
    /// </summary>
    [Fact]
    public async Task Una_sqlexception_di_difetto_non_e_divisibile()
    {
        var session = new FakePurgeSession
        {
            FailAtStatement = 1,
            Failure = SqlExceptionFactory.Create(208, "oggetto inesistente")
        };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None);

        Assert.Equal(SliceOutcome.Fatal, result.Outcome);
        Assert.False(result.Splittable);
    }

    /// <summary>La cancellazione propaga e non diventa un esito.</summary>
    [Fact]
    public async Task La_cancellazione_propaga()
    {
        var session = new FakePurgeSession();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sut(session).ExecuteAsync(Run(), Slice(), cts.Token));

        Assert.Equal(0, session.Commits);
    }

    /// <summary>
    /// Un guasto all'apertura della sessione deve passare dagli stessi quattro
    /// catch: e' la ragione per cui l'apertura sta dentro il try.
    /// </summary>
    [Fact]
    public async Task Un_guasto_all_apertura_viene_classificato()
    {
        var session = new FakePurgeSession();
        var sut = Sut(session, await SqlExceptionFactory.WithNumberAsync(4060));

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => sut.ExecuteAsync(Run(), Slice(), CancellationToken.None));

        Assert.Equal(0, session.Rollbacks);   // non c'era niente da annullare
        Assert.Equal(0, session.Commits);
    }

    // ---------------------------------------------------------- invarianti

    /// <summary>
    /// Se la DELETE dell'ordine ne cancella meno del previsto, un ordine ha
    /// cambiato stato fra selezione ed esecuzione. Procedere lascerebbe a
    /// database un ordine senza storico ne' dettagli. Il checkpoint non deve
    /// essere scritto.
    /// </summary>
    [Fact]
    public async Task Un_rowcount_inferiore_annulla_e_non_scrive_il_checkpoint()
    {
        var session = new FakePurgeSession
        {
            DefaultRowCount = 3,
            RowCountByFragment = { [StatementOrder] = 2 }
        };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(orderCount: 3), CancellationToken.None);

        Assert.Equal(SliceOutcome.Retryable, result.Outcome);
        Assert.Equal("StatusChangedDuringExecution", result.Reason);
        Assert.Equal(1, session.Rollbacks);
        Assert.Equal(0, session.Commits);
        Assert.Equal(0, session.CountContaining("RunBatchProgress"));
        Assert.Equal(0, session.CountContaining("PurgeAudit"));
    }

    /// <summary>
    /// Il commit fallisce dopo che tutto e' passato. Lo stato e' ambiguo — il
    /// commit puo' essere passato o no — ma non serve distinguere: il
    /// checkpoint sta dentro la transazione, quindi le due possibilita'
    /// portano allo stesso comportamento alla ripresa.
    /// </summary>
    [Fact]
    public async Task Un_commit_fallito_su_guasto_transitorio_risale()
    {
        var session = new FakePurgeSession
        {
            CommitFailure = await SqlExceptionFactory.WithNumberAsync(10054)
        };

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None));

        Assert.Equal(0, session.Commits);
        Assert.True(session.Disposed);
    }

    /// <summary>
    /// Un rollback fallito non deve coprire l'errore originale: e' quello che
    /// interessa a chi legge il log la mattina dopo.
    /// </summary>
    [Fact]
    public async Task Un_rollback_fallito_non_copre_l_errore_originale()
    {
        var originale = new InvalidOperationException("causa vera");
        var session = new FakePurgeSession
        {
            FailAtStatement = 1,
            Failure = originale,
            RollbackFailure = new InvalidOperationException("anche il rollback")
        };

        var result = await Sut(session).ExecuteAsync(Run(), Slice(), CancellationToken.None);

        Assert.Equal(SliceOutcome.Fatal, result.Outcome);
        Assert.Equal(originale.Message, result.Reason);
    }
}
