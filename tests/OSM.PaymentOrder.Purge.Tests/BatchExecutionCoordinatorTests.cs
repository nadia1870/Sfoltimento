using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Observability;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

public sealed class BatchExecutionCoordinatorTests
{
    [Fact]
    public async Task No_pending_slices_completes_run()
    {
        var provider = new FakeWorkProvider();
        var executor = new FakeExecutor();
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(1, provider.GetNextCalls);
        Assert.Empty(executor.ExecutedBatches);
    }

    [Fact]
    public async Task Completed_slices_are_counted_and_rows_are_accumulated()
    {
        var provider = new FakeWorkProvider(
            Slice(1, attempt: 0),
            Slice(2, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Ok(10),
            SliceResult.Ok(25));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(35, result.RowsDeleted);
        Assert.Equal(new[] { 1, 2 }, executor.ExecutedBatches);
    }

    [Fact]
    public async Task Retryable_slice_is_retried_without_being_abandoned()
    {
        var provider = new FakeWorkProvider(
            Slice(7, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Retryable("deadlock"),
            SliceResult.Ok(12));
        var sut = CreateSut(provider, executor, maxAttempts: 3);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(12, result.RowsDeleted);
        Assert.Single(provider.RecordedAttempts);
        Assert.Equal((7, "deadlock"), provider.RecordedAttempts[0]);
        Assert.Empty(provider.Abandoned);
    }

    [Fact]
    public async Task Retryable_slice_at_max_attempts_is_abandoned_and_next_slice_continues()
    {
        var provider = new FakeWorkProvider(
            Slice(7, attempt: 2),
            Slice(8, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Retryable("timeout"),
            SliceResult.Ok(20));
        var sut = CreateSut(provider, executor, maxAttempts: 3);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedSlices);
        Assert.Equal(1, result.AbandonedSlices);
        Assert.Equal(20, result.RowsDeleted);
        Assert.Equal(new[] { 7, 8 }, executor.ExecutedBatches);
        Assert.Single(provider.Abandoned);
        Assert.Equal((7, "timeout"), provider.Abandoned[0]);
    }

    [Fact]
    public async Task Fatal_slice_is_abandoned_and_next_slice_continues()
    {
        var provider = new FakeWorkProvider(
            Slice(3, attempt: 0),
            Slice(4, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Fatal("invalid state"),
            SliceResult.Ok(5));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedSlices);
        Assert.Equal(1, result.AbandonedSlices);
        Assert.Equal(5, result.RowsDeleted);
        Assert.Equal((3, "invalid state"), Assert.Single(provider.Abandoned));
    }

    [Fact]
    public async Task Closed_window_suspends_run_without_fetching_work()
    {
        var provider = new FakeWorkProvider(Slice(1, 0));
        var executor = new FakeExecutor(SliceResult.Ok(10));
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 5, 6, 0, 0, TimeSpan.FromHours(2)));
        var sut = CreateSut(
            provider,
            executor,
            clock,
            configure: options =>
            {
                options.WindowEnabled = true;
                options.WindowStart = new TimeOnly(1, 0);
                options.WindowEnd = new TimeOnly(5, 0);
            });

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(0, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(0, provider.GetNextCalls);
        Assert.Empty(executor.ExecutedBatches);
    }

    [Fact]
    public async Task Cancellation_before_execution_does_not_complete_run()
    {
        var provider = new FakeWorkProvider(Slice(1, 0));
        var executor = new FakeExecutor(SliceResult.Ok(10));
        var sut = CreateSut(provider, executor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ExecuteAsync(CreateRun(), cts.Token));

        Assert.Equal(0, provider.GetNextCalls);
        Assert.Empty(executor.ExecutedBatches);
    }

    [Fact]
    public async Task Cancellation_after_a_slice_does_not_return_completed_run()
    {
        var provider = new FakeWorkProvider(Slice(1, 0), Slice(2, 0));
        using var cts = new CancellationTokenSource();
        var executor = new FakeExecutor(
            onExecute: (_, _) =>
            {
                cts.Cancel();
                return SliceResult.Ok(10);
            });
        var sut = CreateSut(provider, executor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ExecuteAsync(CreateRun(), cts.Token));

        Assert.Equal(1, provider.GetNextCalls);
        Assert.Equal(new[] { 1 }, executor.ExecutedBatches);
    }

    private static BatchExecutionCoordinator CreateSut(
        FakeWorkProvider provider,
        FakeExecutor executor,
        TimeProvider? clock = null,
        int maxAttempts = 3,
        Action<PurgeOptions>? configure = null)
    {
        var options = new PurgeOptions
        {
            MaxSliceAttempts = maxAttempts,
            RetryDelay = TimeSpan.Zero,
            InterSliceDelay = TimeSpan.Zero,
            WindowEnabled = false
        };
        configure?.Invoke(options);

        var services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<PurgeMetrics>()
            .BuildServiceProvider();
        var metrics = services.GetRequiredService<PurgeMetrics>();

        return new BatchExecutionCoordinator(
            provider,
            executor,
            metrics,
            Options.Create(options),
            clock ?? TimeProvider.System,
            NullLogger<BatchExecutionCoordinator>.Instance);
    }

    private static PurgeRun CreateRun() => new()
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

    private static SliceInfo Slice(int batchNo, int attempt) => new()
    {
        BatchNo = batchNo,
        OrderCount = 1,
        EstimatedRowCount = 1,
        AttemptCount = attempt,
        IsOversized = false
    };

    private sealed class FakeWorkProvider(params SliceInfo[] slices) : IBatchWorkProvider
    {
        private readonly Queue<SliceInfo> _slices = new(slices);

        public int GetNextCalls { get; private set; }
        public List<(int BatchNo, string Reason)> RecordedAttempts { get; } = [];
        public List<(int BatchNo, string Reason)> Abandoned { get; } = [];

        public Task<SliceInfo?> GetNextAsync(Guid runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            GetNextCalls++;
            return Task.FromResult(_slices.Count == 0 ? null : _slices.Dequeue());
        }

        public Task RecordAttemptAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RecordedAttempts.Add((batchNo, reason ?? string.Empty));

            // The real PurgeRunStore keeps a retryable slice pending.
            // Model that contract by putting the slice back in the queue.
            _slices.Enqueue(new SliceInfo
            {
                BatchNo = batchNo,
                OrderCount = 1,
                EstimatedRowCount = 1,
                AttemptCount = 1,
                IsOversized = false
            });

            return Task.CompletedTask;
        }

        public Task AbandonAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Abandoned.Add((batchNo, reason ?? string.Empty));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExecutor : IBatchExecutor
    {
        private readonly Queue<SliceResult>? _results;
        private readonly Func<PurgeRun, SliceInfo, SliceResult>? _onExecute;

        public FakeExecutor(params SliceResult[] results) => _results = new Queue<SliceResult>(results);

        public FakeExecutor(Func<PurgeRun, SliceInfo, SliceResult> onExecute) => _onExecute = onExecute;

        public List<int> ExecutedBatches { get; } = [];

        public Task<SliceResult> ExecuteAsync(PurgeRun run, SliceInfo slice, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExecutedBatches.Add(slice.BatchNo);
            var result = _onExecute is not null
                ? _onExecute(run, slice)
                : _results!.Dequeue();
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset localNow) : TimeProvider
    {
        private static readonly TimeZoneInfo TestTimeZone =
            TimeZoneInfo.CreateCustomTimeZone(
                "Test+02",
                TimeSpan.FromHours(2),
                "Test +02",
                "Test +02");

        public override TimeZoneInfo LocalTimeZone => TestTimeZone;

        public override DateTimeOffset GetUtcNow() =>
            localNow.ToUniversalTime();
    }
}
