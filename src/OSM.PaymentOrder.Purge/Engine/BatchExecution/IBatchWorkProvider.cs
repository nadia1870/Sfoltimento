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

    /// <summary>
    /// Slice abbandonate del run, tutte, non solo quelle di questa sessione.
    ///
    /// Sta qui e non su PurgeRunStore perche' il coordinatore deve poter
    /// rispondere senza conoscere la persistenza: e' il contratto che
    /// BatchExecutionCoordinatorContractTests verifica per riflessione.
    /// </summary>
    Task<int> CountAbandonedAsync(Guid runId, CancellationToken ct);
}
