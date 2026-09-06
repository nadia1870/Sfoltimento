using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Engine.Phases;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

[Collection("PurgeDatabase")]
public sealed class ExecutingPhaseTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Completed_execution_with_no_abandoned_slices_completes_phase()
    {
        var runId = await CreateRunAsync();
        var coordinator = new StubCoordinator(
            new BatchExecutionResult(true, 1, 0, 10));

        var phase = new ExecutingPhase(coordinator, db.Store);
        var run = await db.Store.LoadAsync(runId, CancellationToken.None);

        var result = await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Equal(RunPhase.Completed, result.NextPhase);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Incomplete_execution_stays_in_phase()
    {
        var runId = await CreateRunAsync();
        var coordinator = new StubCoordinator(
            new BatchExecutionResult(false, 1, 0, 10));

        var phase = new ExecutingPhase(coordinator, db.Store);
        var run = await db.Store.LoadAsync(runId, CancellationToken.None);

        var result = await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Null(result.NextPhase);
    }

    [Fact]
    public async Task Coordinator_receives_same_run()
    {
        var runId = await CreateRunAsync();
        var coordinator = new StubCoordinator(
            new BatchExecutionResult(true, 0, 0, 0));

        var phase = new ExecutingPhase(coordinator, db.Store);
        var run = await db.Store.LoadAsync(runId, CancellationToken.None);

        await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.Same(run, coordinator.Run);
    }

    [Fact]
    public async Task Coordinator_receives_cancellation_token()
    {
        var runId = await CreateRunAsync();
        using var cts = new CancellationTokenSource();

        var coordinator = new StubCoordinator(
            new BatchExecutionResult(false, 0, 0, 0));

        var phase = new ExecutingPhase(coordinator, db.Store);
        var run = await db.Store.LoadAsync(runId, CancellationToken.None);

        await phase.ExecuteAsync(run, cts.Token);

        Assert.Equal(cts.Token, coordinator.Token);
    }

    private async Task<Guid> CreateRunAsync()
    {
        return await db.Store.CreateAsync(
            RetentionStrategy.Terminated,
            db.Options,
            DateTimeOffset.Now,
            CancellationToken.None);
    }

    private sealed class StubCoordinator(BatchExecutionResult result)
        : IBatchExecutionCoordinator
    {
        public PurgeRun? Run { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<BatchExecutionResult> ExecuteAsync(
            PurgeRun run,
            CancellationToken ct)
        {
            Run = run;
            Token = ct;
            return Task.FromResult(result);
        }
    }
}
