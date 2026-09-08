namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>Pure stateful packing algorithm with no SQL or I/O dependency.</summary>
internal sealed class BatchPacker
{
    internal sealed record Candidate(Guid OrderId, int Weight, Guid? CollectiveOrderId);
    internal sealed record Assignment(Guid OrderId, int BatchNo, bool IsOversized, Guid? CollectiveOrderId);

    private readonly int _maxRowsPerBatch;
    private readonly int _maxOrdersPerBatch;
    private readonly List<Candidate> _collectiveBuffer = [];
    private Guid? _currentCollective;
    private int _collectiveWeight;
    private int _batchNo;
    private int _rowsInBatch;
    private int _ordersInBatch;
    private int _oversized;
    private int _total;
    private bool _completed;

    internal BatchPacker(int maxRowsPerBatch, int maxOrdersPerBatch)
    {
        if (maxRowsPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maxRowsPerBatch));
        if (maxOrdersPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maxOrdersPerBatch));
        _maxRowsPerBatch = maxRowsPerBatch;
        _maxOrdersPerBatch = maxOrdersPerBatch;
    }

    internal int Total => _total;
    internal int OversizedCount => _oversized;
    internal int SliceCount => _total == 0 ? 0 : _batchNo + (_rowsInBatch > 0 || _ordersInBatch > 0 ? 1 : 0);

    internal IReadOnlyList<Assignment> Add(Candidate candidate)
    {
        if (_completed) throw new InvalidOperationException("Il BatchPacker e' gia' stato completato.");
        _total++;
        if (candidate.CollectiveOrderId is Guid collectiveId)
        {
            if (_currentCollective is null) _currentCollective = collectiveId;
            else if (_currentCollective != collectiveId)
            {
                var assignments = FlushCollective();
                StartCollective(collectiveId, candidate);
                return assignments;
            }
            _collectiveBuffer.Add(candidate);
            _collectiveWeight += candidate.Weight;
            return [];
        }

        var collectiveAssignments = FlushCollective();
        var standalone = AssignStandalone(candidate);
        if (collectiveAssignments.Count == 0) return [standalone];
        var result = new List<Assignment>(collectiveAssignments.Count + 1);
        result.AddRange(collectiveAssignments);
        result.Add(standalone);
        return result;
    }

    internal IReadOnlyList<Assignment> Complete()
    {
        if (_completed) throw new InvalidOperationException("Il BatchPacker e' gia' stato completato.");
        _completed = true;
        return FlushCollective();
    }

    private void StartCollective(Guid collectiveId, Candidate candidate)
    {
        _currentCollective = collectiveId;
        _collectiveBuffer.Add(candidate);
        _collectiveWeight = candidate.Weight;
    }

    private IReadOnlyList<Assignment> FlushCollective()
    {
        if (_collectiveBuffer.Count == 0) return [];
        var collectiveId = _collectiveBuffer[0].CollectiveOrderId;
        if (collectiveId is null) throw new InvalidOperationException("Collective buffer senza CollectiveOrderId.");
        var collectiveOrders = _collectiveBuffer.Count;
        var isOversized = _collectiveWeight > _maxRowsPerBatch || collectiveOrders > _maxOrdersPerBatch;
        if (isOversized || (_ordersInBatch > 0 && (_rowsInBatch + _collectiveWeight > _maxRowsPerBatch || _ordersInBatch + collectiveOrders > _maxOrdersPerBatch)))
        {
            if (_ordersInBatch > 0) { _batchNo++; _rowsInBatch = 0; _ordersInBatch = 0; }
        }
        var assignments = new List<Assignment>(_collectiveBuffer.Count);
        foreach (var candidate in _collectiveBuffer) assignments.Add(new Assignment(candidate.OrderId, _batchNo, isOversized, collectiveId.Value));
        _rowsInBatch += _collectiveWeight;
        _ordersInBatch += collectiveOrders;
        if (isOversized) { _oversized++; _batchNo++; _rowsInBatch = 0; _ordersInBatch = 0; }
        _collectiveBuffer.Clear();
        _collectiveWeight = 0;
        _currentCollective = null;
        return assignments;
    }

    private Assignment AssignStandalone(Candidate candidate)
    {
        var isOversized = candidate.Weight > _maxRowsPerBatch || 1 > _maxOrdersPerBatch;
        if (isOversized)
        {
            if (_ordersInBatch > 0) { _batchNo++; _rowsInBatch = 0; _ordersInBatch = 0; }
            _oversized++;
            var assignment = new Assignment(candidate.OrderId, _batchNo, true, null);
            _batchNo++; _rowsInBatch = 0; _ordersInBatch = 0;
            return assignment;
        }
        var wouldExceedRows = _rowsInBatch + candidate.Weight > _maxRowsPerBatch;
        var wouldExceedOrders = _ordersInBatch + 1 > _maxOrdersPerBatch;
        if (_ordersInBatch > 0 && (wouldExceedRows || wouldExceedOrders)) { _batchNo++; _rowsInBatch = 0; _ordersInBatch = 0; }
        var result = new Assignment(candidate.OrderId, _batchNo, false, null);
        _rowsInBatch += candidate.Weight;
        _ordersInBatch++;
        return result;
    }
}
