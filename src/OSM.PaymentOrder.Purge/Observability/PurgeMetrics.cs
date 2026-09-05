using System.Diagnostics.Metrics;

namespace OSM.PaymentOrder.Purge.Observability;

/// <summary>
/// Metriche del motore. Rif. v10 §8.
/// Su .NET 8 System.Diagnostics.Metrics e' disponibile nativamente: non serve
/// piu' l'astrazione che era necessaria su netcoreapp3.1.
/// </summary>
public sealed class PurgeMetrics : IDisposable
{
    public const string MeterName = "OSM.PaymentOrder.Purge";

    private readonly Meter _meter;
    private readonly Counter<long> _rowsDeleted;
    private readonly Counter<long> _slicesCompleted;
    private readonly Counter<long> _slicesAbandoned;
    private readonly Counter<long> _candidatesExcluded;
    private readonly Histogram<double> _sliceDuration;
    private readonly Histogram<int> _sliceRows;

    private long _backlog;

    public PurgeMetrics(IMeterFactory factory)
    {
        _meter = factory.Create(MeterName);

        _rowsDeleted     = _meter.CreateCounter<long>("purge.rows_deleted");
        _slicesCompleted = _meter.CreateCounter<long>("purge.slices_completed");
        _slicesAbandoned = _meter.CreateCounter<long>("purge.slices_abandoned");
        _candidatesExcluded = _meter.CreateCounter<long>("purge.candidates_excluded");
        _sliceDuration   = _meter.CreateHistogram<double>("purge.slice_duration", "s");

        // Verifica in esercizio che il bin packing stia bilanciando (§6.3).
        _sliceRows = _meter.CreateHistogram<int>("purge.slice_rows", "rows");

        // Metrica di salute piu' significativa: una crescita monotona indica
        // che il ritmo dello sfoltimento non regge quello di accumulo, con
        // largo anticipo rispetto alla saturazione dello spazio disco.
        _meter.CreateObservableGauge("purge.backlog_orders", () => Interlocked.Read(ref _backlog));
    }

    public void RowsDeleted(string table, string strategy, int count) =>
        _rowsDeleted.Add(count, new("table", table), new("strategy", strategy));

    public void SliceCompleted(TimeSpan duration, int rows, string strategy)
    {
        _slicesCompleted.Add(1, new KeyValuePair<string, object?>("strategy", strategy));
        _sliceDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("strategy", strategy));
        _sliceRows.Record(rows, new KeyValuePair<string, object?>("strategy", strategy));
    }

    public void SliceAbandoned(string reason) =>
        _slicesAbandoned.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void CandidatesExcluded(string reason, long count) =>
        _candidatesExcluded.Add(count, new KeyValuePair<string, object?>("reason", reason));

    public void SetBacklog(long eligibleOrders) => Interlocked.Exchange(ref _backlog, eligibleOrders);

    public void Dispose() => _meter.Dispose();
}
