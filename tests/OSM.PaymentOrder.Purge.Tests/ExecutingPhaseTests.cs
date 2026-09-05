using Microsoft.Extensions.Logging.Abstractions;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Engine.Phases;

namespace OSM.PaymentOrder.Purge.Tests;

public sealed class ExecutingPhaseTests
{
    [Fact]
    public async Task Completato_senza_collective_tail_transita_a_completed()
    {
        var coordinator = new FakeCoordinator(BatchExecutionResult.CompletedRun(2, 0, 17));
        var resolver = CreateResolver(RetentionStrategy.Terminated, requiresCollectiveTail: false);
        var phase = new ExecutingPhase(coordinator, resolver);
        var run = CreateRun(RetentionStrategy.Terminated);

        var result = await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunPhase.Completed, result.NextPhase);
        Assert.True(result.Stop);
        Assert.Null(result.Error);
        Assert.Equal(1, coordinator.Calls);
    }

    [Fact]
    public async Task Completato_con_collective_tail_transita_a_collective_tail()
    {
        var coordinator = new FakeCoordinator(BatchExecutionResult.CompletedRun(1, 0, 8));
        var resolver = CreateResolver(RetentionStrategy.Collective, requiresCollectiveTail: true);
        var phase = new ExecutingPhase(coordinator, resolver);
        var run = CreateRun(RetentionStrategy.Collective);

        var result = await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunPhase.CollectiveTail, result.NextPhase);
        Assert.False(result.Stop);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Finestra_chiusa_mantiene_il_run_in_executing()
    {
        var coordinator = new FakeCoordinator(BatchExecutionResult.WindowClosed(3, 0, 25));
        var resolver = CreateResolver(RetentionStrategy.Terminated, requiresCollectiveTail: false);
        var phase = new ExecutingPhase(coordinator, resolver);
        var run = CreateRun(RetentionStrategy.Terminated);

        var result = await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.Null(result.NextPhase);
        Assert.True(result.Stop);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CancellationToken_viene_propagato_al_coordinator()
    {
        var coordinator = new FakeCoordinator(BatchExecutionResult.CompletedRun(0, 0, 0));
        var resolver = CreateResolver(RetentionStrategy.Terminated, requiresCollectiveTail: false);
        var phase = new ExecutingPhase(coordinator, resolver);
        var run = CreateRun(RetentionStrategy.Terminated);
        using var cts = new CancellationTokenSource();

        await phase.ExecuteAsync(run, cts.Token);

        Assert.Equal(cts.Token, coordinator.ReceivedToken);
    }

    [Fact]
    public async Task Errore_del_coordinator_non_viene_assorbito_dalla_phase()
    {
        var coordinator = new FakeCoordinator(
            new InvalidOperationException("boom"));
        var resolver = CreateResolver(RetentionStrategy.Terminated, requiresCollectiveTail: false);
        var phase = new ExecutingPhase(coordinator, resolver);
        var run = CreateRun(RetentionStrategy.Terminated);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => phase.ExecuteAsync(run, CancellationToken.None));

        Assert.Equal("boom", ex.Message);
    }

    private static PurgeRun CreateRun(RetentionStrategy strategy) =>
        new()
        {
            RunId = Guid.NewGuid(),
            Strategy = strategy,
            Phase = RunPhase.Executing,
            DryRun = false,
            AnchorMode = RetentionAnchorMode.RollingDate,
            RetentionCutoff = DateTime.UtcNow.AddYears(-2),
            MaxRowsPerBatch = 3000,
            MaxOrdersPerBatch = 500
        };

    private sealed class FakeCoordinator : IBatchExecutionCoordinator
    {
        private readonly BatchExecutionResult? _result;
        private readonly Exception? _exception;

        public int Calls { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public FakeCoordinator(BatchExecutionResult result) => _result = result;
        public FakeCoordinator(Exception exception) => _exception = exception;

        public Task<BatchExecutionResult> ExecuteAsync(PurgeRun run, CancellationToken ct)
        {
            Calls++;
            ReceivedToken = ct;
            if (_exception is not null)
                throw _exception;

            return Task.FromResult(_result!);
        }
    }

    private static PurgeStrategyResolver CreateResolver(
        RetentionStrategy strategy,
        bool requiresCollectiveTail) =>
        new(new[] { new FakeStrategy(strategy, requiresCollectiveTail) });

    private sealed class FakeStrategy(
        RetentionStrategy type,
        bool requiresCollectiveTail) : IPurgeStrategy
    {
        public RetentionStrategy Type => type;
        public PurgePlanningMode PlanningMode => PurgePlanningMode.Standard;
        public bool RequiresCollectiveTail => requiresCollectiveTail;
        public bool SkipCollectiveLinkValidation => false;
        public bool UsesAbandonedDeletes => false;
        public DateTime CutoffOf(PurgeRun run) => run.RetentionCutoff;
        public Task<int> SelectAsync(PurgeRun run, CancellationToken ct) => Task.FromResult(0);
        public Task ExpandAsync(PurgeRun run, CancellationToken ct) => Task.CompletedTask;
        public IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
            Array.Empty<(string Table, string Sql)>();
    }
}
