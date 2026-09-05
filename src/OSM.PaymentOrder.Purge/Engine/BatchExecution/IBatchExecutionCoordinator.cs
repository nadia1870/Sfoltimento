using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.BatchExecution;

public interface IBatchExecutionCoordinator
{
    Task<BatchExecutionResult> ExecuteAsync(
        PurgeRun run,
        CancellationToken ct);
}
