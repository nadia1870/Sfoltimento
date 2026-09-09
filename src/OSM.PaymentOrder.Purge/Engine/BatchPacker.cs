namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Algoritmo di bin packing delle slice.
///
/// Non e' una funzione pura: e' un accumulatore incrementale con stato, ed e'
/// la forma giusta per uno stream di candidati che non sta in memoria. Le due
/// proprieta' che contano, e su cui si reggono i test, sono altre: e'
/// deterministico — la stessa sequenza di Add produce sempre le stesse
/// assegnazioni — e non fa I/O.
///
/// Il chiamante deve rispettare un solo contratto: i candidati dello stesso
/// CollectiveOrderId devono arrivare contigui. E' l'ordinamento delle query di
/// lettura a garantirlo; se salta, ValidateCollectiveBatchIntegrity intercetta
/// il collettivo spezzato in esecuzione, ma la slice viene abbandonata.
/// </summary>
internal sealed class BatchPacker
{
    internal sealed record Candidate(Guid OrderId, int Weight, Guid? CollectiveOrderId);
    /// <summary>
    /// Assegnazione di un candidato a una slice. Con Excluded a vero
    /// l'aggregato non viene assegnato ad alcuna slice: supera
    /// MaxAggregateWeight e resta a database, censito.
    /// </summary>
    internal sealed record Assignment(
        Guid OrderId, int BatchNo, bool IsOversized, Guid? CollectiveOrderId, bool Excluded = false);

    private readonly int _maxRowsPerBatch;
    private readonly int _maxOrdersPerBatch;
    private readonly int _maxAggregateWeight;
    private readonly List<Candidate> _collectiveBuffer = [];
    private Guid? _currentCollective;
    private int _collectiveWeight;
    private int _batchNo;
    private int _rowsInBatch;
    private int _ordersInBatch;
    private int _oversized;
    private int _tooLarge;
    private int _total;
    private bool _completed;

    /// <param name="maxAggregateWeight">
    /// Peso oltre il quale l'aggregato non viene assegnato ma escluso. Zero
    /// disattiva il limite e ripristina il comportamento precedente: ogni
    /// aggregato, quanto grande sia, diventa una slice dedicata.
    /// </param>
    internal BatchPacker(int maxRowsPerBatch, int maxOrdersPerBatch, int maxAggregateWeight = 0)
    {
        if (maxRowsPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maxRowsPerBatch));
        if (maxOrdersPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maxOrdersPerBatch));
        if (maxAggregateWeight < 0) throw new ArgumentOutOfRangeException(nameof(maxAggregateWeight));

        // Un limite piu' basso del tetto della slice renderebbe indecidibile
        // il caso intermedio: un aggregato sopra MaxRowsPerBatch dovrebbe
        // essere insieme slice dedicata ed escluso.
        if (maxAggregateWeight > 0 && maxAggregateWeight < maxRowsPerBatch)
            throw new ArgumentOutOfRangeException(nameof(maxAggregateWeight),
                "MaxAggregateWeight non puo' essere minore di MaxRowsPerBatch.");

        _maxRowsPerBatch = maxRowsPerBatch;
        _maxOrdersPerBatch = maxOrdersPerBatch;
        _maxAggregateWeight = maxAggregateWeight;
    }

    internal int Total => _total;
    internal int OversizedCount => _oversized;

    /// <summary>Aggregati esclusi perche' oltre MaxAggregateWeight.</summary>
    internal int TooLargeCount => _tooLarge;

    private bool IsTooLarge(int weight) => _maxAggregateWeight > 0 && weight > _maxAggregateWeight;
    /// <summary>
    /// Numero di slice prodotte. Ha significato solo dopo <see cref="Complete"/>:
    /// letto prima, non conta i collettivi ancora nel buffer.
    /// </summary>
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

        // Oltre il tetto per aggregato il collettivo non entra in nessuna
        // slice: verrebbe cancellato in una sola transazione, e a quel peso la
        // transazione e' il problema. Resta a database e viene censito.
        if (IsTooLarge(_collectiveWeight))
        {
            var esclusi = new List<Assignment>(_collectiveBuffer.Count);
            foreach (var c in _collectiveBuffer)
                esclusi.Add(new Assignment(c.OrderId, BatchNo: 0, IsOversized: false,
                                           collectiveId.Value, Excluded: true));
            _tooLarge++;
            _collectiveBuffer.Clear();
            _collectiveWeight = 0;
            _currentCollective = null;
            return esclusi;
        }

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
        // Un candidato standalone e' un ordine solo, quindi il tetto sul numero
        // di ordini per slice non puo' essere superato: l'unico modo di essere
        // oversized e' il peso in righe.
        // Stesso tetto dei collettivi: un ordine con migliaia di revisioni
        // resta a database invece di diventare una transazione ingestibile.
        if (IsTooLarge(candidate.Weight))
        {
            _tooLarge++;
            return new Assignment(candidate.OrderId, BatchNo: 0, IsOversized: false,
                                  CollectiveOrderId: null, Excluded: true);
        }

        var isOversized = candidate.Weight > _maxRowsPerBatch;
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
