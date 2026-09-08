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
    /// Divide la slice in due figlie per aggregato e le mette in coda (D-11).
    /// Restituisce quante figlie ha creato. Zero significa che la slice
    /// conteneva un aggregato solo e non e' stata toccata: e' il segnale per
    /// abbandonare, perche' a quel punto l'abbandono e' circoscritto al
    /// colpevole. Il coordinatore decide *se* dividere; come si divide e'
    /// affare della persistenza.
    /// </summary>
    Task<int> SplitAsync(
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

    /// <summary>
    /// Segnala che il run ha fatto progresso, azzerando le interruzioni
    /// accumulate.
    ///
    /// Passa da qui e non da SliceExecutor di proposito: il contatore
    /// appartiene al ciclo di vita del run, mentre SliceExecutor deve
    /// continuare a occuparsi solo dell'esecuzione atomica di una slice.
    /// </summary>
    Task ReportProgressAsync(Guid runId, CancellationToken ct);
}
