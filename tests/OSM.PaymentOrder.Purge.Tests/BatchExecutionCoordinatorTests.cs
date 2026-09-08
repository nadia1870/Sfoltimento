using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Observability;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

[Trait("Category", "Unit")]
public sealed class BatchExecutionCoordinatorTests
{
    [Fact]
    public async Task No_pending_slices_completes_run()
    {
        var provider = new FakeWorkProvider();
        var executor = new FakeExecutor();
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(1, provider.GetNextCalls);
        Assert.Empty(executor.ExecutedBatches);
    }

    [Fact]
    public async Task Completed_slices_are_counted_and_rows_are_accumulated()
    {
        var provider = new FakeWorkProvider(
            Slice(1, attempt: 0),
            Slice(2, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Ok(10),
            SliceResult.Ok(25));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(35, result.RowsDeleted);
        Assert.Equal(new[] { 1, 2 }, executor.ExecutedBatches);
    }

    [Fact]
    public async Task Retryable_slice_is_retried_without_being_abandoned()
    {
        var provider = new FakeWorkProvider(
            Slice(7, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Retryable("deadlock"),
            SliceResult.Ok(12));
        var sut = CreateSut(provider, executor, maxAttempts: 3);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(12, result.RowsDeleted);
        Assert.Single(provider.RecordedAttempts);
        Assert.Equal((7, "deadlock"), provider.RecordedAttempts[0]);
        Assert.Empty(provider.Abandoned);
    }

    [Fact]
    public async Task Retryable_slice_at_max_attempts_is_abandoned_and_next_slice_continues()
    {
        var provider = new FakeWorkProvider(
            Slice(7, attempt: 2),
            Slice(8, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Retryable("timeout"),
            SliceResult.Ok(20));
        var sut = CreateSut(provider, executor, maxAttempts: 3);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedSlices);
        Assert.Equal(1, result.AbandonedSlices);
        Assert.Equal(20, result.RowsDeleted);
        Assert.Equal(new[] { 7, 8 }, executor.ExecutedBatches);
        Assert.Single(provider.Abandoned);
        Assert.Equal((7, "timeout"), provider.Abandoned[0]);
    }

    [Fact]
    public async Task Fatal_slice_is_abandoned_and_next_slice_continues()
    {
        var provider = new FakeWorkProvider(
            Slice(3, attempt: 0),
            Slice(4, attempt: 0));
        var executor = new FakeExecutor(
            SliceResult.Fatal("invalid state"),
            SliceResult.Ok(5));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedSlices);
        Assert.Equal(1, result.AbandonedSlices);
        Assert.Equal(5, result.RowsDeleted);
        Assert.Equal((3, "invalid state"), Assert.Single(provider.Abandoned));
        Assert.Empty(provider.Splits);
    }

    // ------------------------------------------------------ bisezione D-11

    /// <summary>
    /// Quattro aggregati, il terzo rifiuta la cancellazione. Senza bisezione
    /// se ne perderebbero quattro; con la bisezione se ne perde uno, e
    /// l'abbandono finale riguarda una slice da un aggregato solo. Il
    /// lavoro extra e' due split, non quattro transazioni singole.
    /// </summary>
    [Fact]
    public async Task Un_errore_di_dati_divide_la_slice_e_abbandona_solo_il_colpevole()
    {
        const int colpevole = 3;
        var provider = new FakeWorkProvider(SliceWith(orders: 4, batchNo: 1));
        var executor = new FakeExecutor((_, slice) =>
            provider.AggregatesOf(slice.BatchNo).Contains(colpevole)
                ? SliceResult.Fatal("FK", splittable: true)
                : SliceResult.Ok(slice.OrderCount));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedSlices);     // {1,2} e {4}
        Assert.Equal(3, result.RowsDeleted);
        Assert.Equal(1, result.AbandonedSlices);
        Assert.Equal(1, result.AbandonedTotal);

        var abbandonata = Assert.Single(provider.Abandoned);
        Assert.Equal(1, provider.OrderCountOf(abbandonata.BatchNo));
        Assert.Equal(new[] { colpevole }, provider.AggregatesOf(abbandonata.BatchNo));

        // Due divisioni con figlie: {1,2,3,4} e {3,4}. La terza richiesta,
        // sulla slice {3}, torna a vuoto ed e' quella che porta all'abbandono:
        // il coordinatore non sa quanti aggregati contiene una slice — un
        // collettivo da tre ordini e' un aggregato solo — e lo chiede allo store.
        Assert.Equal(2, provider.Splits.Count(s => s.Children > 0));
        Assert.Equal((abbandonata.BatchNo, 0), provider.Splits.Last());
    }

    /// <summary>
    /// La bisezione non si applica a un esito che non la dichiara: un
    /// difetto del programma fallirebbe ogni figlia, e dividerlo
    /// moltiplicherebbe soltanto le transazioni fallite.
    /// </summary>
    [Fact]
    public async Task Un_esito_non_divisibile_viene_abbandonato_in_blocco()
    {
        var provider = new FakeWorkProvider(SliceWith(orders: 4, batchNo: 1));
        var executor = new FakeExecutor((_, _) => SliceResult.Fatal("difetto", splittable: false));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(1, result.AbandonedSlices);
        Assert.Empty(provider.Splits);
        Assert.Equal(4, provider.OrderCountOf(Assert.Single(provider.Abandoned).BatchNo));
    }

    /// <summary>MaxSplitDepth a zero disattiva la bisezione e ripristina l'abbandono in blocco.</summary>
    [Fact]
    public async Task Con_profondita_zero_non_si_divide_mai()
    {
        var provider = new FakeWorkProvider(SliceWith(orders: 4, batchNo: 1));
        var executor = new FakeExecutor((_, _) => SliceResult.Fatal("FK", splittable: true));
        var sut = CreateSut(provider, executor, configure: o => o.MaxSplitDepth = 0);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(1, result.AbandonedSlices);
        Assert.Empty(provider.Splits);
    }

    /// <summary>
    /// Il freno in profondita': con un difetto che si spaccia per errore di
    /// dati, ogni figlia fallisce. Oltre MaxSplitDepth si smette di dividere
    /// e si abbandona cio' che resta, anche se contiene piu' di un aggregato.
    /// </summary>
    [Fact]
    public async Task Oltre_la_profondita_massima_si_abbandona_anche_se_divisibile()
    {
        var provider = new FakeWorkProvider(SliceWith(orders: 8, batchNo: 1));
        var executor = new FakeExecutor((_, _) => SliceResult.Fatal("FK", splittable: true));
        var sut = CreateSut(provider, executor, configure: o => o.MaxSplitDepth = 1);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        // Una sola divisione (8 -> 4+4), poi le due figlie a profondita' 1
        // vengono abbandonate senza dividerle ancora.
        Assert.Single(provider.Splits);
        Assert.Equal(2, result.AbandonedSlices);
        Assert.All(provider.Abandoned, a => Assert.Equal(4, provider.OrderCountOf(a.BatchNo)));
    }

    /// <summary>
    /// Una slice da un aggregato solo e' gia' il caso circoscritto: lo store
    /// restituisce zero figlie e il coordinatore abbandona senza contare una
    /// divisione avvenuta.
    /// </summary>
    [Fact]
    public async Task Una_slice_da_un_aggregato_solo_viene_abbandonata_direttamente()
    {
        var provider = new FakeWorkProvider(SliceWith(orders: 1, batchNo: 1));
        var executor = new FakeExecutor((_, _) => SliceResult.Fatal("FK", splittable: true));
        var sut = CreateSut(provider, executor);

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(1, result.AbandonedSlices);
        Assert.Equal((1, 0), Assert.Single(provider.Splits));
        Assert.Equal(1, Assert.Single(provider.Abandoned).BatchNo);
    }

    /// <summary>
    /// Le figlie vanno in coda, non davanti: la slice pendente successiva
    /// viene eseguita prima di tornare sul dubbio.
    /// </summary>
    [Fact]
    public async Task Le_figlie_della_divisione_vanno_dopo_le_slice_pendenti()
    {
        var provider = new FakeWorkProvider(
            SliceWith(orders: 2, batchNo: 1),
            SliceWith(orders: 1, batchNo: 2));
        var executor = new FakeExecutor((_, slice) =>
            slice.BatchNo == 1
                ? SliceResult.Fatal("FK", splittable: true)
                : SliceResult.Ok(slice.OrderCount));
        var sut = CreateSut(provider, executor);

        await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3, 4 }, executor.ExecutedBatches);
    }

    [Fact]
    public async Task Closed_window_suspends_run_without_fetching_work()
    {
        var provider = new FakeWorkProvider(Slice(1, 0));
        var executor = new FakeExecutor(SliceResult.Ok(10));
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 5, 6, 0, 0, TimeSpan.FromHours(2)));
        var sut = CreateSut(
            provider,
            executor,
            clock,
            configure: options =>
            {
                options.WindowEnabled = true;
                options.WindowStart = new TimeOnly(1, 0);
                options.WindowEnd = new TimeOnly(5, 0);
            });

        var result = await sut.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(0, result.CompletedSlices);
        Assert.Equal(0, result.AbandonedSlices);
        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(0, provider.GetNextCalls);
        Assert.Empty(executor.ExecutedBatches);
    }

    [Fact]
    public async Task Cancellation_before_execution_does_not_complete_run()
    {
        var provider = new FakeWorkProvider(Slice(1, 0));
        var executor = new FakeExecutor(SliceResult.Ok(10));
        var sut = CreateSut(provider, executor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ExecuteAsync(CreateRun(), cts.Token));

        Assert.Equal(0, provider.GetNextCalls);
        Assert.Empty(executor.ExecutedBatches);
    }

    [Fact]
    public async Task Cancellation_after_a_slice_does_not_return_completed_run()
    {
        var provider = new FakeWorkProvider(Slice(1, 0), Slice(2, 0));
        using var cts = new CancellationTokenSource();
        var executor = new FakeExecutor(
            onExecute: (_, _) =>
            {
                cts.Cancel();
                return SliceResult.Ok(10);
            });
        var sut = CreateSut(provider, executor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ExecuteAsync(CreateRun(), cts.Token));

        Assert.Equal(1, provider.GetNextCalls);
        Assert.Equal(new[] { 1 }, executor.ExecutedBatches);
    }

    private static BatchExecutionCoordinator CreateSut(
        FakeWorkProvider provider,
        FakeExecutor executor,
        TimeProvider? clock = null,
        int maxAttempts = 3,
        Action<PurgeOptions>? configure = null)
    {
        var options = new PurgeOptions
        {
            MaxSliceAttempts = maxAttempts,
            RetryDelay = TimeSpan.Zero,
            InterSliceDelay = TimeSpan.Zero,
            WindowEnabled = false
        };
        configure?.Invoke(options);

        var services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<PurgeMetrics>()
            .BuildServiceProvider();
        var metrics = services.GetRequiredService<PurgeMetrics>();

        return new BatchExecutionCoordinator(
            provider,
            executor,
            metrics,
            Options.Create(options),
            clock ?? TimeProvider.System,
            NullLogger<BatchExecutionCoordinator>.Instance);
    }

    private static PurgeRun CreateRun() => new()
    {
        RunId = Guid.NewGuid(),
        Strategy = RetentionStrategy.Terminated,
        Phase = RunPhase.Executing,
        DryRun = false,
        AnchorMode = RetentionAnchorMode.RollingDate,
        RetentionCutoff = DateTime.UtcNow,
        MaxRowsPerBatch = 3000,
        MaxOrdersPerBatch = 500
    };

    private static SliceInfo Slice(int batchNo, int attempt) => new()
    {
        BatchNo = batchNo,
        OrderCount = 1,
        EstimatedRowCount = 1,
        AttemptCount = attempt,
        IsOversized = false
    };

    private static SliceInfo SliceWith(int orders, int batchNo) => new()
    {
        BatchNo = batchNo,
        OrderCount = orders,
        EstimatedRowCount = orders,
        AttemptCount = 0,
        IsOversized = false
    };

    /// <summary>
    /// Un'eccezione dell'esecutore deve attraversare il coordinatore senza
    /// essere tradotta in un abbandono.
    ///
    /// E' l'anello centrale della catena che porta un guasto di connessione
    /// dalla slice all'orchestratore. Se il coordinatore la catturasse, o se
    /// SliceExecutor la trasformasse in Fatal, la slice verrebbe abbandonata e
    /// il run chiuderebbe in CompletedWithErrors: gli aggregati resterebbero a
    /// database in via definitiva per un guasto passeggero.
    /// </summary>
    [Fact]
    public async Task Executor_exception_propagates_without_abandoning()
    {
        var guasto = new TimeoutException("connessione caduta");
        var workProvider = new FakeWorkProvider(Slice(1, 0), Slice(2, 0));
        var coordinator = CreateSut(workProvider, FakeExecutor.Throwing(guasto));

        var sollevata = await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.ExecuteAsync(CreateRun(), CancellationToken.None));

        Assert.Same(guasto, sollevata);
        Assert.Empty(workProvider.Abandoned);
        Assert.Empty(workProvider.RecordedAttempts);
    }

    /// <summary>
    /// A2 — il progresso si segnala una volta per sessione, non per slice.
    ///
    /// Il contatore vive su una riga condivisa di PurgeRun: azzerarlo a ogni
    /// slice sarebbe una scrittura per slice, migliaia per run, per registrare
    /// un'informazione che dopo la prima non cambia piu'.
    /// </summary>
    [Fact]
    public async Task Il_progresso_si_segnala_una_volta_sola_per_sessione()
    {
        var slices = Enumerable.Range(0, 20).Select(n => Slice(n, 0)).ToArray();
        var workProvider = new FakeWorkProvider(slices);
        var coordinator = CreateSut(workProvider, new FakeExecutor((_, _) => SliceResult.Ok(1)));

        var result = await coordinator.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(20, result.CompletedSlices);
        Assert.Equal(1, workProvider.ProgressReports);
    }

    /// <summary>
    /// Nessuna slice completata, nessun progresso da segnalare: un run che
    /// abbandona tutto non ha fatto passi avanti, e le interruzioni delle notti
    /// precedenti devono restare contate.
    /// </summary>
    [Fact]
    public async Task Senza_slice_completate_non_si_segnala_progresso()
    {
        var workProvider = new FakeWorkProvider(Slice(1, 0), Slice(2, 0));
        var coordinator = CreateSut(
            workProvider, new FakeExecutor((_, _) => SliceResult.Fatal("difetto")));

        await coordinator.ExecuteAsync(CreateRun(), CancellationToken.None);

        Assert.Equal(0, workProvider.ProgressReports);
        Assert.Equal(2, workProvider.Abandoned.Count);
    }

    private sealed class FakeWorkProvider(params SliceInfo[] slices) : IBatchWorkProvider
    {
        private readonly Queue<SliceInfo> _slices = new(slices);

        /// <summary>
        /// Aggregati per slice. Il fake modella lo store: per dividere una
        /// slice deve sapere cosa contiene, e SliceInfo non lo dice. Le
        /// slice date al costruttore ricevono aggregati numerati da 1 in
        /// base a OrderCount.
        /// </summary>
        private readonly Dictionary<int, List<int>> _aggregates =
            slices.ToDictionary(
                s => s.BatchNo,
                s => Enumerable.Range(1, s.OrderCount).ToList());

        private int _nextBatchNo = slices.Length == 0 ? 0 : slices.Max(s => s.BatchNo) + 1;

        public int GetNextCalls { get; private set; }
        public List<(int BatchNo, string Reason)> RecordedAttempts { get; } = [];
        public List<(int BatchNo, string Reason)> Abandoned { get; } = [];
        public List<(int BatchNo, int Children)> Splits { get; } = [];

        public IReadOnlyList<int> AggregatesOf(int batchNo) => _aggregates[batchNo];

        /// <summary>OrderCount della slice abbandonata: deve essere 1 se la bisezione ha isolato il colpevole.</summary>
        public int OrderCountOf(int batchNo) => _aggregates[batchNo].Count;

        public Task<SliceInfo?> GetNextAsync(Guid runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            GetNextCalls++;
            return Task.FromResult(_slices.Count == 0 ? null : _slices.Dequeue());
        }

        /// <summary>
        /// Stesso contratto dello statement SplitSlice: due figlie per
        /// aggregato, in coda, con profondita' + 1; zero se l'aggregato e'
        /// uno solo, e in quel caso non tocca niente.
        /// </summary>
        public Task<int> SplitAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var aggregates = _aggregates[batchNo];
            if (aggregates.Count < 2)
            {
                Splits.Add((batchNo, 0));
                return Task.FromResult(0);
            }

            var depth = _depths.GetValueOrDefault(batchNo);
            var half = aggregates.Count / 2;

            foreach (var part in new[] { aggregates.Take(half).ToList(), aggregates.Skip(half).ToList() })
            {
                var child = _nextBatchNo++;
                _aggregates[child] = part;
                _depths[child] = depth + 1;
                _slices.Enqueue(new SliceInfo
                {
                    BatchNo = child,
                    OrderCount = part.Count,
                    EstimatedRowCount = part.Count,
                    AttemptCount = 0,
                    IsOversized = false,
                    SplitDepth = depth + 1
                });
            }

            Splits.Add((batchNo, 2));
            return Task.FromResult(2);
        }

        private readonly Dictionary<int, int> _depths = [];

        public Task RecordAttemptAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RecordedAttempts.Add((batchNo, reason ?? string.Empty));

            // The real PurgeRunStore keeps a retryable slice pending.
            // Model that contract by putting the slice back in the queue.
            _slices.Enqueue(new SliceInfo
            {
                BatchNo = batchNo,
                OrderCount = 1,
                EstimatedRowCount = 1,
                AttemptCount = 1,
                IsOversized = false
            });

            return Task.CompletedTask;
        }

        public Task AbandonAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Abandoned.Add((batchNo, reason ?? string.Empty));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Deliberatamente non restituisce zero: il fake modella lo store, che
        /// conta le slice abbandonate del run. PreviousSessionAbandoned simula
        /// quelle lasciate da una finestra operativa precedente.
        /// </summary>
        public int PreviousSessionAbandoned { get; init; }

        /// <summary>
        /// Quante volte il coordinatore ha segnalato progresso. Deve essere una
        /// sola per sessione, a prescindere dal numero di slice.
        /// </summary>
        public int ProgressReports { get; private set; }

        public Task ReportProgressAsync(Guid runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ProgressReports++;
            return Task.CompletedTask;
        }

        public Task<int> CountAbandonedAsync(Guid runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Abandoned.Count + PreviousSessionAbandoned);
        }
    }

    private sealed class FakeExecutor : IBatchExecutor
    {
        private readonly Queue<SliceResult>? _results;
        private readonly Func<PurgeRun, SliceInfo, SliceResult>? _onExecute;

        public FakeExecutor(params SliceResult[] results) => _results = new Queue<SliceResult>(results);

        public FakeExecutor(Func<PurgeRun, SliceInfo, SliceResult> onExecute) => _onExecute = onExecute;

        /// <summary>
        /// Solleva invece di restituire un esito. Serve a verificare che il
        /// coordinatore non converta un'eccezione in un abbandono: un guasto
        /// di connessione deve risalire, non lasciare aggregati a database.
        /// </summary>
        public static FakeExecutor Throwing(Exception ex) =>
            new((_, _) => throw ex);

        public List<int> ExecutedBatches { get; } = [];

        public Task<SliceResult> ExecuteAsync(PurgeRun run, SliceInfo slice, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExecutedBatches.Add(slice.BatchNo);
            var result = _onExecute is not null
                ? _onExecute(run, slice)
                : _results!.Dequeue();
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset localNow) : TimeProvider
    {
        private static readonly TimeZoneInfo TestTimeZone =
            TimeZoneInfo.CreateCustomTimeZone(
                "Test+02",
                TimeSpan.FromHours(2),
                "Test +02",
                "Test +02");

        public override TimeZoneInfo LocalTimeZone => TestTimeZone;

        public override DateTimeOffset GetUtcNow() =>
            localNow.ToUniversalTime();
    }
}
