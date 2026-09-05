using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Observability;

namespace OSM.PaymentOrder.Purge.Engine.BatchExecution;

/// <summary>
/// Coordina l'esecuzione delle slice di un PurgeRun.
/// 
/// Questa classe contiene esclusivamente la politica di esecuzione dei batch:
/// finestra operativa, recupero della prossima slice, retry, abandon,
/// metriche e pacing tra le slice. La cancellazione SQL atomica resta
/// responsabilità di SliceExecutor.
/// 
/// È il seam naturale per una futura implementazione asincrona/distribuita
/// (es. RabbitMQ): ExecutingPhase non deve conoscere il meccanismo di dispatch.
/// </summary>
public sealed class BatchExecutionCoordinator(
    IBatchWorkProvider workProvider,
    IBatchExecutor executor,
    PurgeMetrics metrics,
    IOptions<PurgeOptions> options,
    TimeProvider clock,
    ILogger<BatchExecutionCoordinator> log) : IBatchExecutionCoordinator
{
    private readonly PurgeOptions _options = options.Value;

    public async Task<BatchExecutionResult> ExecuteAsync(
        PurgeRun run,
        CancellationToken ct)
    {
        var completed = 0;
        var abandoned = 0;
        long totalRows = 0;

        while (true)
        {
            // A cancellation is a control-flow signal, not a successful run completion.
            // Check it explicitly before each iteration so we never fall through to
            // CompletedRun after the token has been cancelled.
            ct.ThrowIfCancellationRequested();

            if (!_options.IsWithinWindow(clock.GetLocalNow()))
            {
                log.LogInformation(
                    "Fine finestra operativa: RunId={RunId} sospeso, slice completate={Completed}",
                    run.RunId,
                    completed);

                return BatchExecutionResult.WindowClosed(
                    completed,
                    abandoned,
                    totalRows);
            }

            var slice = await workProvider.GetNextAsync(
                run.RunId,
                ct).ConfigureAwait(false);

            if (slice is null)
                break;

            var result = await executor.ExecuteAsync(
                run,
                slice,
                ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SliceOutcome.Completed:
                    completed++;
                    totalRows += result.RowsDeleted;
                    break;

                case SliceOutcome.Retryable
                    when slice.AttemptCount + 1 < _options.MaxSliceAttempts:

                    await workProvider.RecordAttemptAsync(
                        run.RunId,
                        slice.BatchNo,
                        result.Reason,
                        ct).ConfigureAwait(false);

                    await Task.Delay(
                        _options.RetryDelay,
                        ct).ConfigureAwait(false);
                    break;

                default:
                    // Un singolo aggregato problematico non deve bloccare
                    // l'intero sfoltimento.
                    await workProvider.AbandonAsync(
                        run.RunId,
                        slice.BatchNo,
                        result.Reason,
                        ct).ConfigureAwait(false);

                    metrics.SliceAbandoned(
                        result.Reason ?? "unknown");

                    abandoned++;
                    break;
            }

            await Task.Delay(
                _options.InterSliceDelay,
                ct).ConfigureAwait(false);
        }

        log.LogInformation(
            "PurgeRunCompleted RunId={RunId} Righe={Rows} SliceCompletate={Completed} " +
            "SliceAbbandonate={Abandoned}",
            run.RunId,
            totalRows,
            completed,
            abandoned);

        if (abandoned > 0)
        {
            log.LogWarning(
                "RunId={RunId}: {Count} slice abbandonate, richiedono analisi.",
                run.RunId,
                abandoned);
        }

        return BatchExecutionResult.CompletedRun(
            completed,
            abandoned,
            totalRows);
    }
}

public sealed record BatchExecutionResult(
    bool Completed,
    int CompletedSlices,
    int AbandonedSlices,
    long RowsDeleted)
{
    public static BatchExecutionResult WindowClosed(
        int completedSlices,
        int abandonedSlices,
        long rowsDeleted) =>
        new(false, completedSlices, abandonedSlices, rowsDeleted);

    public static BatchExecutionResult CompletedRun(
        int completedSlices,
        int abandonedSlices,
        long rowsDeleted) =>
        new(true, completedSlices, abandonedSlices, rowsDeleted);
}
