using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Engine.BatchExecution;

public sealed class PurgeRunBatchWorkProvider(PurgeRunStore store) : IBatchWorkProvider
{
    public Task<SliceInfo?> GetNextAsync(Guid runId, CancellationToken ct) =>
        store.NextPendingSliceAsync(runId, ct);

    public Task RecordAttemptAsync(
        Guid runId,
        int batchNo,
        string? reason,
        CancellationToken ct) =>
        store.RecordAttemptAsync(runId, batchNo, reason, ct);

    public Task AbandonAsync(
        Guid runId,
        int batchNo,
        string? reason,
        CancellationToken ct) =>
        store.AbandonSliceAsync(runId, batchNo, reason, ct);
}
