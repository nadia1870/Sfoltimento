using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.Phases;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Contratti della macchina a stati a livello di orchestratore.
///
/// I test qui presenti non duplicano i test delle singole Phase o del
/// BatchExecutionCoordinator: verificano il confine fra PhaseResult,
/// persistenza della fase e successiva ripresa del run.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class RetentionOrchestratorStateMachineTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MUST_Executing_Stay_then_next_invocation_resumes_pending_slice()
    {
        await SeedOrderAsync();

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated,
            db.Options,
            DateTimeOffset.Now,
            default);

        // Porta il run fino a Executing senza eseguire le slice.
        var run = await db.Store.LoadAsync(runId, default);
        foreach (var phaseName in new[]
                 {
                     RunPhase.Selecting,
                     RunPhase.Expanding,
                     RunPhase.Validating,
                     RunPhase.Planning
                 })
        {
            var phase = db.Services.GetServices<IPurgePhase>()
                .Single(p => p.Phase == phaseName);
            var result = await phase.ExecuteAsync(run, default);

            Assert.NotNull(result.NextPhase);
            await db.Store.SetPhaseAsync(run.RunId, result.NextPhase!.Value, default, result.Error);
            run.Phase = result.NextPhase.Value;
        }

        Assert.Equal(RunPhase.Executing, run.Phase);

        var originalWindowEnabled = db.Options.WindowEnabled;
        var originalWindowStart = db.Options.WindowStart;
        var originalWindowEnd = db.Options.WindowEnd;

        try
        {
            // Costruisce una finestra di un'ora che non contiene l'istante
            // corrente. Gestisce anche il passaggio oltre mezzanotte.
            var now = TimeProvider.System.GetLocalNow();
            var closedStart = new TimeOnly((now.Hour + 2) % 24, now.Minute);
            var closedEnd = closedStart.AddHours(1);

            db.Options.WindowEnabled = true;
            db.Options.WindowStart = closedStart;
            db.Options.WindowEnd = closedEnd;

            await db.Orchestrator.RunAsync(runId, default);

            var suspended = await db.Store.LoadAsync(runId, default);
            Assert.Equal(RunPhase.Executing, suspended.Phase);

            var pending = await db.Sql.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM Purge.RunBatchProgress WHERE RunId = @RunId AND Status = 'Pending';",
                default,
                SqlParam.Of("@RunId", runId));
            Assert.True(pending > 0);

            // La seconda invocation usa lo stesso run/checkpoint. Aprendo la
            // finestra, il coordinator deve consumare la slice già pianificata.
            db.Options.WindowEnabled = false;
            await db.Orchestrator.RunAsync(runId, default);

            var completed = await db.Store.LoadAsync(runId, default);
            Assert.Equal(RunPhase.Completed, completed.Phase);
            Assert.Equal(0, await db.Sql.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM Purge.RunBatchProgress WHERE RunId = @RunId AND Status = 'Pending';",
                default,
                SqlParam.Of("@RunId", runId)));
        }
        finally
        {
            db.Options.WindowEnabled = originalWindowEnabled;
            db.Options.WindowStart = originalWindowStart;
            db.Options.WindowEnd = originalWindowEnd;
        }
    }

    [Fact]
    public async Task IMPORTANT_cancellation_preserves_the_current_phase_checkpoint()
    {
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated,
            db.Options,
            DateTimeOffset.Now,
            default);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.Orchestrator.RunAsync(runId, cts.Token));

        var persisted = await db.Store.LoadAsync(runId, default);
        Assert.Equal(RunPhase.Created, persisted.Phase);
    }

    [Fact]
    public async Task IMPORTANT_successful_phase_transition_is_persisted_before_next_phase_runs()
    {
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated,
            db.Options,
            DateTimeOffset.Now,
            default);

        var transitionPhase = new StubPhase(
            RunPhase.Created,
            RunPhase.Created,
            PhaseResult.Next(RunPhase.Expanding));

        var cancellationPhase = new StubPhase(
            RunPhase.Expanding,
            RunPhase.Expanding,
            exception: new OperationCanceledException());

        var orchestrator = new RetentionOrchestrator(
            db.Store,
            new IPurgePhase[] { transitionPhase, cancellationPhase },
            db.Services.GetRequiredService<PurgeStrategyResolver>(),
            Options.Create(new PurgeOptions()), 
            NullLogger<RetentionOrchestrator>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.RunAsync(runId, default));

        var persisted = await db.Store.LoadAsync(runId, default);

        // La prima phase ha completato il proprio lavoro e la transizione è
        // stata persistita. La seconda è stata cancellata: non deve avanzare.
        Assert.Equal(RunPhase.Expanding, persisted.Phase);
        Assert.Equal(1, transitionPhase.Calls);
        Assert.Equal(1, cancellationPhase.Calls);
    }

    [Fact]
    public async Task SHOULD_coordinator_does_not_fetch_work_after_the_final_slice_without_extra_pacing()
    {
        // Questo è il contratto minimo che vogliamo preservare quando si
        // ottimizzerà InterSliceDelay: una sola slice implica una sola fetch
        // iniziale e una fetch finale per determinare l'esaurimento del lavoro.
        var provider = new RecordingWorkProvider(Slice(1));
        var executor = new RecordingExecutor();
        var clock = new RecordingTimeProvider();
        var sut = new OSM.PaymentOrder.Purge.Engine.BatchExecution.BatchExecutionCoordinator(
            provider,
            executor,
            db.Services.GetRequiredService<OSM.PaymentOrder.Purge.Observability.PurgeMetrics>(),
            Microsoft.Extensions.Options.Options.Create(new PurgeOptions
            {
                WindowEnabled = false,
                InterSliceDelay = TimeSpan.FromSeconds(5),
                RetryDelay = TimeSpan.Zero,
                MaxSliceAttempts = 3
            }),
            clock,
            NullLogger<OSM.PaymentOrder.Purge.Engine.BatchExecution.BatchExecutionCoordinator>.Instance);

        var result = await sut.ExecuteAsync(CreateRun(), default);

        Assert.True(result.Completed);
        Assert.Equal(2, provider.GetNextCalls);
        Assert.Single(executor.ExecutedBatches);
        Assert.Empty(clock.Delays);
    }

    private async Task SeedOrderAsync()
    {
        var seed = new SeedBuilder(db.Sql);
        await seed.AddOrderAsync(revisions: 2);
    }

    private static PurgeRun CreateRun() => new()
    {
        RunId = Guid.NewGuid(),
        Strategy = RetentionStrategy.Terminated,
        Phase = RunPhase.Executing,
        DryRun = false,
        AnchorMode = RetentionAnchorMode.RollingDate,
        RetentionCutoff = DateTime.UtcNow,
        MaxRowsPerBatch = 50,
        MaxOrdersPerBatch = 10
    };

    private static SliceInfo Slice(int batchNo) => new()
    {
        BatchNo = batchNo,
        OrderCount = 1,
        EstimatedRowCount = 1,
        AttemptCount = 0,
        IsOversized = false
    };

    private sealed class StubPhase(
        RunPhase phase,
        RunPhase handledPhase,
        PhaseResult? result = null,
        Exception? exception = null) : IPurgePhase
    {
        public RunPhase Phase => phase;
        public IReadOnlySet<RunPhase> HandledPhases => new HashSet<RunPhase> { handledPhase };
        public int Calls { get; private set; }

        public Task<PhaseResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
        {
            Calls++;
            if (exception is not null)
                throw exception;

            return Task.FromResult(result!);
        }
    }

    private sealed class RecordingWorkProvider(SliceInfo slice) : OSM.PaymentOrder.Purge.Engine.BatchExecution.IBatchWorkProvider
    {
        private int _calls;

        public int GetNextCalls => _calls;

        public Task<SliceInfo?> GetNextAsync(Guid runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _calls++;
            return Task.FromResult<SliceInfo?>(_calls == 1 ? slice : null);
        }

        public Task RecordAttemptAsync(Guid runId, int batchNo, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task AbandonAsync(Guid runId, int batchNo, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        /// <summary>
        /// Nessuna slice abbandonata: questi test provano le transizioni di fase,
        /// non gli esiti dell'esecuzione. Un valore diverso da zero manderebbe ogni
        /// run in CompletedWithErrors e le assert sulle transizioni fallirebbero
        /// per il motivo sbagliato.
        /// </summary>
        public Task<int> CountAbandonedAsync(Guid runId, CancellationToken ct) =>
            Task.FromResult(0);
    }


    private sealed class RecordingTimeProvider : TimeProvider
    {
        private static readonly TimeZoneInfo TestTimeZone =
            TimeZoneInfo.CreateCustomTimeZone(
                "Test+02",
                TimeSpan.FromHours(2),
                "Test +02",
                "Test +02");

        public List<TimeSpan> Delays { get; } = [];

        public override TimeZoneInfo LocalTimeZone => TestTimeZone;

        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Add(dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new ImmediateTimer();
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingExecutor : OSM.PaymentOrder.Purge.Engine.BatchExecution.IBatchExecutor
    {
        public List<int> ExecutedBatches { get; } = [];

        public Task<SliceResult> ExecuteAsync(PurgeRun run, SliceInfo slice, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExecutedBatches.Add(slice.BatchNo);
            return Task.FromResult(SliceResult.Ok(1));
        }
    }
}
