using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Engine.Phases;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

public sealed class ExecutingPhaseTests
{
    [Fact]
    public async Task Completed_without_collective_tail_completes_phase()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 1, 0, 10));
        var phase = new ExecutingPhase(coordinator, CreateResolver(RetentionStrategy.Terminated, false));

        var result = await phase.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Equal(RunPhase.Completed, result.NextPhase);
    }

    [Fact]
    public async Task Completed_with_collective_tail_moves_to_collective_tail()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 1, 0, 10));
        var phase = new ExecutingPhase(coordinator, CreateResolver(RetentionStrategy.Collective, true));

        var result = await phase.ExecuteAsync(CreateRun(RetentionStrategy.Collective), CancellationToken.None);

        Assert.False(result.Stop);
        Assert.Equal(RunPhase.CollectiveTail, result.NextPhase);
    }

    [Fact]
    public async Task Incomplete_execution_stays_in_phase()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(false, 1, 0, 10));
        var phase = new ExecutingPhase(coordinator, CreateResolver(RetentionStrategy.Terminated, false));

        var result = await phase.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Null(result.NextPhase);
    }

    [Fact]
    public async Task Coordinator_receives_same_run()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 0, 0, 0));
        var phase = new ExecutingPhase(coordinator, CreateResolver(RetentionStrategy.Terminated, false));
        var run = CreateRun();

        await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.Same(run, coordinator.Run);
    }

    [Fact]
    public async Task Coordinator_receives_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 0, 0, 0));
        var phase = new ExecutingPhase(coordinator, CreateResolver(RetentionStrategy.Terminated, false));

        await phase.ExecuteAsync(CreateRun(), cts.Token);

        Assert.Equal(cts.Token, coordinator.Token);
    }

    private static PurgeRun CreateRun(RetentionStrategy strategy = RetentionStrategy.Terminated) => new()
    {
        RunId = Guid.NewGuid(),
        Strategy = strategy,
        Phase = RunPhase.Executing,
        DryRun = false,
        AnchorMode = RetentionAnchorMode.RollingDate,
        RetentionCutoff = DateTime.UtcNow,
        AbandonedCutoff = null,
        MaxRowsPerBatch = 50,
        MaxOrdersPerBatch = 10
    };

    private static PurgeStrategyResolver CreateResolver(RetentionStrategy strategy, bool requiresCollectiveTail) =>
        new(new[] { new FakeStrategy(strategy, requiresCollectiveTail) });

    private sealed class StubCoordinator(BatchExecutionResult result) : IBatchExecutionCoordinator
    {
        public PurgeRun? Run { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<BatchExecutionResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
        {
            Run = run;
            Token = ct;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeStrategy(RetentionStrategy type, bool requiresCollectiveTail) : IPurgeStrategy
    {
        public RetentionStrategy Type => type;
        public PurgePlanningMode PlanningMode => PurgePlanningMode.Standard;
        public bool RequiresCollectiveTail => requiresCollectiveTail;
        public bool SkipCollectiveLinkValidation => false;
        public bool UsesAbandonedDeletes => false;
        public DateTime CutoffOf(PurgeRun run) => run.RetentionCutoff;
        public Task<int> SelectAsync(PurgeRun run, CancellationToken ct) => Task.FromResult(0);
        public Task ExpandAsync(PurgeRun run, CancellationToken ct) => Task.CompletedTask;
        public IEnumerable<(string Table, string Sql)> GetSliceStatements() => Array.Empty<(string, string)>();
    }
}
