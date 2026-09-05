using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.BatchExecution;

public sealed class SliceBatchExecutor(SliceExecutor executor) : IBatchExecutor
{
    public Task<SliceResult> ExecuteAsync(
        PurgeRun run,
        SliceInfo slice,
        CancellationToken ct) =>
        executor.ExecuteAsync(run, slice, ct);
}
