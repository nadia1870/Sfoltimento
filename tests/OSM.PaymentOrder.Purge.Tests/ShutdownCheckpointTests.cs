using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine.Phases;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// A3 — la persistenza di uno stato gia' deciso non e' lavoro cancellabile.
///
/// Interrompere una fase alla chiusura della finestra e' corretto. Non scrivere
/// che quella fase e' stata raggiunta non lo e': il run riprenderebbe da una
/// fase diversa da quella in cui si e' fermato, e il checkpoint su cui si regge
/// tutta la ripresa direbbe il falso.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class ShutdownCheckpointTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Fase che avanza e poi si fa cancellare, come uno spegnimento che arriva
    /// subito dopo che la fase ha finito il proprio lavoro.
    /// </summary>
    private sealed class FaseCheAvanzaPoiVieneCancellata(
        RunPhase gestita, RunPhase successiva, CancellationTokenSource cts) : IPurgePhase
    {
        public RunPhase Phase => gestita;

        public IReadOnlySet<RunPhase> HandledPhases { get; } =
            gestita == RunPhase.Selecting
                ? new HashSet<RunPhase> { RunPhase.Created, RunPhase.Selecting }
                : new HashSet<RunPhase> { gestita };

        public Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
        {
            // Il lavoro e' fatto e la fase successiva e' decisa. Lo spegnimento
            // arriva ora: la scrittura non deve saltare.
            cts.Cancel();
            return Task.FromResult(PhaseResult.Next(successiva));
        }
    }

    /// <summary>
    /// Il token cancellato non deve impedire la scrittura della fase raggiunta.
    /// Con la vecchia versione, che passava il token del lavoro a
    /// SetPhaseAsync, il run restava in Selecting e la notte dopo rifaceva la
    /// selezione da capo.
    /// </summary>
    [Fact]
    public async Task La_fase_raggiunta_viene_scritta_anche_a_token_cancellato()
    {
        await new SeedBuilder(db.Sql).AddOrderAsync(revisions: 1);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        using var cts = new CancellationTokenSource();
        var orchestratore = db.OrchestratorWith(
            new FaseCheAvanzaPoiVieneCancellata(
                RunPhase.Selecting, RunPhase.Expanding, cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestratore.RunAsync(runId, cts.Token));

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(RunPhase.Expanding, run.Phase);
        Assert.Equal(runId, await db.Store.FindResumableAsync(
            RetentionStrategy.Terminated, default));
    }

    /// <summary>
    /// La cancellazione resta una cancellazione: non conta come interruzione da
    /// guasto, perche' la chiusura della finestra e' il funzionamento previsto.
    /// </summary>
    [Fact]
    public async Task Lo_spegnimento_non_consuma_una_ripresa()
    {
        await new SeedBuilder(db.Sql).AddOrderAsync(revisions: 1);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        using var cts = new CancellationTokenSource();
        var orchestratore = db.OrchestratorWith(
            new FaseCheAvanzaPoiVieneCancellata(
                RunPhase.Selecting, RunPhase.Expanding, cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestratore.RunAsync(runId, cts.Token));

        var interruzioni = await db.Sql.ScalarAsync<int>(
            "SELECT InterruptionCount FROM Purge.PurgeRun WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(0, interruzioni);
    }
}
