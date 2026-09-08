using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

[Trait("Category", "Unit")]
public sealed class BatchPackerTests
{
    private static List<BatchPacker.Assignment> AddAll(
        BatchPacker sut,
        IEnumerable<BatchPacker.Candidate> candidates)
    {
        var result = new List<BatchPacker.Assignment>();

        foreach (var candidate in candidates)
        {
            result.AddRange(sut.Add(candidate));
        }

        result.AddRange(sut.Complete());

        return result;
    }

    private static BatchPacker.Candidate Candidate(int weight = 1, Guid? collective = null) =>
        new(Guid.NewGuid(), weight, collective);

    [Fact]
    public void Empty_input_has_zero_slices()
    {
        var sut = new BatchPacker(100, 10);

        sut.Complete();

        Assert.Equal(0, sut.Total);
        Assert.Equal(0, sut.SliceCount);
    }

    [Fact]
    public void Standalone_candidates_are_packed_without_exceeding_limits()
    {
        var sut = new BatchPacker(10, 3);
        var candidates = Enumerable.Range(0, 7)
            .Select(_ => Candidate(3))
            .ToArray();

        var assignments = AddAll(sut, candidates);

        Assert.Equal(candidates.Length, assignments.Count);

        foreach (var group in assignments.GroupBy(a => a.BatchNo))
        {
            Assert.True(
                group.Sum(a => candidates.Single(c => c.OrderId == a.OrderId).Weight) <= 10);

            Assert.True(group.Count() <= 3);
        }
    }

    [Fact]
    public void Every_candidate_is_assigned_exactly_once()
    {
        var sut = new BatchPacker(100, 10);
        var candidates = Enumerable.Range(0, 25)
            .Select(_ => Candidate(4))
            .ToArray();

        var assignments = AddAll(sut, candidates);

        Assert.Equal(candidates.Length, assignments.Count);
        Assert.Equal(
            candidates.Length,
            assignments.Select(a => a.OrderId).Distinct().Count());
    }

    [Fact]
    public void Oversized_candidate_is_alone_in_its_batch()
    {
        var sut = new BatchPacker(10, 10);
        var first = Candidate(5);
        var oversized = Candidate(11);
        var third = Candidate(2);

        var assignments = AddAll(sut, new[] { first, oversized, third });

        var assignment = Assert.Single(
            assignments,
            x => x.OrderId == oversized.OrderId);

        Assert.True(assignment.IsOversized);
        Assert.Single(assignments, x => x.BatchNo == assignment.BatchNo);
    }

    [Fact]
    public void Collective_stays_in_one_batch()
    {
        var sut = new BatchPacker(100, 10);
        var id = Guid.NewGuid();

        var candidates = new[]
        {
            Candidate(10, id),
            Candidate(10, id),
            Candidate(10, id)
        };

        var assignments = AddAll(sut, candidates);

        Assert.Equal(3, assignments.Count);
        Assert.Single(assignments.Select(a => a.BatchNo).Distinct());
        Assert.All(assignments, a => Assert.Equal(id, a.CollectiveOrderId));
    }

    [Fact]
    public void Collective_isolated_when_it_would_exceed_current_batch()
    {
        var sut = new BatchPacker(10, 10);
        var first = Candidate(4);
        var id = Guid.NewGuid();

        var collective = new[]
        {
            Candidate(4, id),
            Candidate(4, id)
        };

        var assignments = AddAll(
            sut,
            new[] { first }.Concat(collective));

        var firstBatch = Assert.Single(
            assignments,
            a => a.OrderId == first.OrderId).BatchNo;

        var collectiveBatch = Assert.Single(
            assignments
                .Where(a => a.CollectiveOrderId == id)
                .Select(a => a.BatchNo)
                .Distinct());

        Assert.NotEqual(firstBatch, collectiveBatch);
    }

    [Fact]
    public void Batch_numbers_are_contiguous()
    {
        var sut = new BatchPacker(10, 10);
        var candidates = Enumerable.Range(0, 9)
            .Select(_ => Candidate(4))
            .ToArray();

        var assignments = AddAll(sut, candidates);

        var batches = assignments
            .Select(a => a.BatchNo)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(Enumerable.Range(0, batches.Length), batches);
    }

    /// <summary>
    /// SliceCount e' il numero che finisce nel log di planning e che nessun
    /// altro ricalcola. Contarlo con un'espressione a parte, invece che dalle
    /// assegnazioni prodotte, e' il tipo di codice che resta corretto per caso:
    /// qui lo si lega ai batch realmente emessi.
    /// </summary>
    [Theory]
    // solo standalone, ultimo batch parziale
    [InlineData(9, 0, false)]
    // numero di candidati multiplo esatto della capienza del batch
    [InlineData(8, 0, false)]
    // un collettivo in coda
    [InlineData(5, 3, false)]
    // aggregato oversized come ultimo elemento: chiude il batch e non ne apre uno vuoto
    [InlineData(5, 0, true)]
    // oversized come unico elemento
    [InlineData(0, 0, true)]
    public void SliceCount_matches_the_batches_actually_emitted(
        int standalone,
        int collectiveComponents,
        bool trailingOversized)
    {
        var sut = new BatchPacker(10, 4);
        var candidates = new List<BatchPacker.Candidate>();

        for (var i = 0; i < standalone; i++)
            candidates.Add(Candidate(4));

        if (collectiveComponents > 0)
        {
            var id = Guid.NewGuid();
            for (var i = 0; i < collectiveComponents; i++)
                candidates.Add(Candidate(2, id));
        }

        if (trailingOversized)
            candidates.Add(Candidate(25));

        var assignments = AddAll(sut, candidates);

        Assert.Equal(
            assignments.Select(a => a.BatchNo).Distinct().Count(),
            sut.SliceCount);

        // Contiguita' da zero: un buco significherebbe una slice che nessuno
        // eseguira' mai, e RunBatchProgress la conterebbe comunque.
        Assert.Equal(
            Enumerable.Range(0, sut.SliceCount),
            assignments.Select(a => a.BatchNo).Distinct().OrderBy(x => x));
    }

    /// <summary>
    /// Il planning viene rieseguito da capo dopo un'interruzione. Se la stessa
    /// sequenza producesse assegnazioni diverse, la ripianificazione non
    /// sarebbe una ripetizione ma un piano nuovo, e il confronto fra dry-run
    /// approvato ed esecuzione reale perderebbe significato.
    /// </summary>
    [Fact]
    public void Packing_is_deterministic()
    {
        var collective = Guid.NewGuid();
        var candidates = new List<BatchPacker.Candidate>();

        for (var i = 0; i < 12; i++)
            candidates.Add(Candidate(1 + i % 5));

        for (var i = 0; i < 4; i++)
            candidates.Add(Candidate(3, collective));

        var first = AddAll(new BatchPacker(10, 4), candidates);
        var second = AddAll(new BatchPacker(10, 4), candidates);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Completing_twice_is_rejected()
    {
        var sut = new BatchPacker(10, 10);

        sut.Complete();

        Assert.Throws<InvalidOperationException>(() => sut.Complete());
    }
}
