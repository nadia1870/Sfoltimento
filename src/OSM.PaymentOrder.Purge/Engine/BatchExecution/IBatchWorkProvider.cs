using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.BatchExecution;

public interface IBatchWorkProvider
{
    Task<SliceInfo?> GetNextAsync(Guid runId, CancellationToken ct);

    Task RecordAttemptAsync(
        Guid runId,
        int batchNo,
        string? reason,
        CancellationToken ct);

    Task AbandonAsync(
        Guid runId,
        int batchNo,
        string? reason,
        CancellationToken ct);
}
