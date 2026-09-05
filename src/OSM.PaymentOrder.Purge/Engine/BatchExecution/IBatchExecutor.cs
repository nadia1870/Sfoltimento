using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.BatchExecution;

public interface IBatchExecutor
{
    Task<SliceResult> ExecuteAsync(
        PurgeRun run,
        SliceInfo slice,
        CancellationToken ct);
}
