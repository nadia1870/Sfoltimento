using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Engine.Phases;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

[Trait("Category", "Unit")]
public sealed class ExecutingPhaseTests
{
    [Fact]
    public async Task Completed_execution_completes_phase()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 1, 0, 10));
        var phase = new ExecutingPhase(coordinator);

        var result = await phase.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Equal(RunPhase.Completed, result.NextPhase);
    }


    [Fact]
    public async Task Incomplete_execution_stays_in_phase()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(false, 1, 0, 10));
        var phase = new ExecutingPhase(coordinator);

        var result = await phase.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Null(result.NextPhase);
    }

    [Fact]
    public async Task Coordinator_receives_same_run()
    {
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 0, 0, 0));
        var phase = new ExecutingPhase(coordinator);
        var run = CreateRun();

        await phase.ExecuteAsync(run, CancellationToken.None);

        Assert.Same(run, coordinator.Run);
    }

    [Fact]
    public async Task Coordinator_receives_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 0, 0, 0));
        var phase = new ExecutingPhase(coordinator);

        await phase.ExecuteAsync(CreateRun(), cts.Token);

        Assert.Equal(cts.Token, coordinator.Token);
    }

    [Fact]
    public async Task Abandoned_slices_complete_phase_with_errors()
    {
        // Quinto argomento: gli abbandoni dell'intero run, non della sessione.
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 3, 1, 10, 1));
        var phase = new ExecutingPhase(coordinator);

        var result = await phase.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Stop);
        Assert.Equal(RunPhase.CompletedWithErrors, result.NextPhase);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Abandons_from_a_previous_session_still_count()
    {
        // Nessun abbandono in questa sessione, uno nel run: chiudere in
        // Completed perderebbe l'informazione.
        var coordinator = new StubCoordinator(new BatchExecutionResult(true, 5, 0, 40, 1));
        var phase = new ExecutingPhase(coordinator);

        var result = await phase.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(RunPhase.CompletedWithErrors, result.NextPhase);
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

  
}
