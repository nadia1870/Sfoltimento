using System.Data;
using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Incapsula le differenze fra gli scenari di retention.
/// Il workflow, lo staging Purge.*, il validation, il planning e il checkpoint
/// restano responsabilita' dei componenti comuni.
/// </summary>
public interface IPurgeStrategy
{
    RetentionStrategy Type { get; }

    /// <summary>Indica quale algoritmo di planning deve usare BatchPlanner.</summary>
    PurgePlanningMode PlanningMode { get; }

    /// <summary>Determina se il candidato puo' essere legato a un collective.</summary>
    bool SkipCollectiveLinkValidation { get; }

    /// <summary>Determina se la DELETE dell'Order deve applicare la variante abandoned.</summary>
    bool UsesAbandonedDeletes { get; }

    /// <summary>
    /// Quale delle soglie congelate sul run si applica a questa strategia.
    /// Stava su PurgeRun come switch sull'enum: e' una decisione della
    /// strategia, non una proprieta' del modello del run.
    /// </summary>
    DateTime CutoffOf(PurgeRun run);

    Task<int> SelectAsync(PurgeRun run, CancellationToken ct);
    Task ExpandAsync(PurgeRun run, CancellationToken ct);

    /// <summary>Statement di cancellazione della slice, nell'ordine FK-safe.</summary>
    IEnumerable<(string Table, string Sql)> GetSliceStatements();
}

public enum PurgePlanningMode
{
    Standard,
    OrphanHistory
}

public abstract class PurgeStrategyBase(
    BatchedStatementRunner batched,
    ILogger log) : IPurgeStrategy
{
    public abstract RetentionStrategy Type { get; }
    public virtual PurgePlanningMode PlanningMode => PurgePlanningMode.Standard;
    public virtual bool SkipCollectiveLinkValidation => false;
    public virtual bool UsesAbandonedDeletes => false;

    public virtual DateTime CutoffOf(PurgeRun run) => run.RetentionCutoff;

    public abstract Task<int> SelectAsync(PurgeRun run, CancellationToken ct);

    public virtual async Task ExpandAsync(PurgeRun run, CancellationToken ct)
    {
        var histories = await batched.RunAsync(
            $"PurgeExpansion{Type}",
            run.RunId,
            RetentionSql.ExpandAndWeigh,
            ct).ConfigureAwait(false);

        log.LogInformation(
            "PurgeExpansionCompleted RunId={RunId} Strategy={Strategy} Storici={Histories}",
            run.RunId, Type, histories);
    }

    public virtual IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
        RetentionSql.SliceStatements(UsesAbandonedDeletes);

    protected Task<int> ExecuteSelectionAsync(
        string statement,
        PurgeRun run,
        CancellationToken ct) =>
        batched.RunAsync(
            $"PurgeSelection{Type}",
            run.RunId,
            statement,
            ct,
            SqlParam.Typed("@Cutoff", CutoffOf(run), SqlDbType.DateTime2));
}

public sealed class TerminatedStrategy(BatchedStatementRunner batched, ILogger<TerminatedStrategy> log)
    : PurgeStrategyBase(batched, log)
{
    public override RetentionStrategy Type => RetentionStrategy.Terminated;

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var inserted = await ExecuteSelectionAsync(RetentionSql.SelectTerminated, run, ct)
            .ConfigureAwait(false);
        log.LogInformation("PurgeSelectionCompleted RunId={RunId} Strategy={Strategy} Candidati={Count}",
            run.RunId, Type, inserted);
        return inserted;
    }
}

public sealed class AbandonedStrategy(BatchedStatementRunner batched, ILogger<AbandonedStrategy> log)
    : PurgeStrategyBase(batched, log)
{
    public override DateTime CutoffOf(PurgeRun run) =>
        run.AbandonedCutoff ?? throw new InvalidOperationException(
            "Run con strategia Abandoned privo di AbandonedCutoff.");

    public override RetentionStrategy Type => RetentionStrategy.Abandoned;
    public override bool UsesAbandonedDeletes => true;

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var inserted = await ExecuteSelectionAsync(RetentionSql.SelectAbandoned, run, ct)
            .ConfigureAwait(false);
        log.LogInformation("PurgeSelectionCompleted RunId={RunId} Strategy={Strategy} Candidati={Count}",
            run.RunId, Type, inserted);
        return inserted;
    }
}

public sealed class StandingOrdersStrategy(BatchedStatementRunner batched, ILogger<StandingOrdersStrategy> log)
    : PurgeStrategyBase(batched, log)
{
    public override RetentionStrategy Type => RetentionStrategy.StandingOrders;

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var inserted = await ExecuteSelectionAsync(RetentionSql.SelectStandingOrders, run, ct)
            .ConfigureAwait(false);
        log.LogInformation("PurgeSelectionCompleted RunId={RunId} Strategy={Strategy} Candidati={Count}",
            run.RunId, Type, inserted);
        return inserted;
    }
}

/// <summary>
/// I collettivi restano su statement singoli, non paginati.
///
/// Non e' una dimenticanza. La selezione collettiva ha invarianti che
/// attraversano l'intero insieme: ValidateOrderBelongsToSingleCollective
/// verifica che nessun ordine appartenga a due collettivi eleggibili, e
/// SelectCollectiveComponents deve vedere tutti i collettivi selezionati.
/// Paginare qui significa decidere cosa voglia dire quell'invariante su una
/// selezione ancora incompleta, ed e' una decisione che va presa a parte.
/// La popolazione dei collettivi e' inoltre di un altro ordine di grandezza
/// rispetto a quella degli ordini.
/// </summary>
public sealed class CollectiveStrategy(
    ISqlExecutor sql,
    BatchedStatementRunner batched,
    ILogger<CollectiveStrategy> log)
    : PurgeStrategyBase(batched, log)
{
    private readonly ISqlExecutor _sql = sql;
    public override RetentionStrategy Type => RetentionStrategy.Collective;
    // Il Collective viene eliminato nella stessa transazione dei suoi ordini componenti.
    // La coda dell'aggregato collettivo e' inclusa negli statement della slice:
    // non esiste una cancellazione differita in una fase successiva.
    public override bool SkipCollectiveLinkValidation => true;

    public override IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
        RetentionSql.SliceStatements(abandoned: false)
            .Concat(RetentionSql.CollectiveSliceStatements());

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var p = new[]
        {
            SqlParam.Of("@RunId", run.RunId),
            SqlParam.Typed("@Cutoff", CutoffOf(run), SqlDbType.DateTime2)
        };

        var eligible = await _sql.ExecuteAsync(RetentionSql.SelectEligibleCollectives, ct, p)
            .ConfigureAwait(false);

        // Manteniamo il censimento degli anomalie della V3: e' una scrittura
        // intenzionale in Purge.RunCandidateCollective e non va persa nel refactoring.
        var withoutDate = await _sql.ExecuteAsync(
            RetentionSql.SelectCollectivesWithoutDate,
            ct,
            SqlParam.Of("@RunId", run.RunId)).ConfigureAwait(false);

        if (withoutDate > 0)
        {
            log.LogWarning(
                "RunId={RunId}: {Count} collettivi privi di ExecutionDate, esclusi e censiti.",
                run.RunId, withoutDate);
        }

        var ambiguousOrders = await _sql.ScalarAsync<long>(
            RetentionSql.ValidateOrderBelongsToSingleCollective,
            ct,
            SqlParam.Of("@RunId", run.RunId)).ConfigureAwait(false);

        if (ambiguousOrders > 0)
            throw new InvalidOperationException(
                $"Run {run.RunId}: {ambiguousOrders} ordini appartengono a piu' Collective eleggibili; " +
                "il purge atomico non puo' essere pianificato in modo sicuro.");

        var components = await _sql.ExecuteAsync(
            RetentionSql.SelectCollectiveComponents,
            ct,
            SqlParam.Of("@RunId", run.RunId)).ConfigureAwait(false);

        log.LogInformation(
            "RunId={RunId} Strategy={Strategy} CollettiviEleggibili={Eligible} Componenti={Components}",
            run.RunId, Type, eligible, components);

        // Stesso evento delle altre strategie: un formato diverso rompe le
        // query sui log e gli alert costruiti su questo nome.
        log.LogInformation("PurgeSelectionCompleted RunId={RunId} Strategy={Strategy} Candidati={Count}",
            run.RunId, Type, components);

        return components;
    }
}

public sealed class OrphanHistoryStrategy(BatchedStatementRunner batched, ILogger<OrphanHistoryStrategy> log)
    : PurgeStrategyBase(batched, log)
{
    public override RetentionStrategy Type => RetentionStrategy.OrphanHistory;
    public override PurgePlanningMode PlanningMode => PurgePlanningMode.OrphanHistory;

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var inserted = await ExecuteSelectionAsync(RetentionSql.SelectOrphanHistory, run, ct)
            .ConfigureAwait(false);
        log.LogInformation("PurgeSelectionCompleted RunId={RunId} Strategy={Strategy} Candidati={Count}",
            run.RunId, Type, inserted);
        return inserted;
    }

    public override IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
        RetentionSql.OrphanSliceStatements();
}

/// <summary>Risoluzione centralizzata della strategy tramite DI.</summary>
public sealed class PurgeStrategyResolver(IEnumerable<IPurgeStrategy> strategies)
{
    private readonly IReadOnlyDictionary<RetentionStrategy, IPurgeStrategy> _strategies =
        strategies.ToDictionary(x => x.Type);

    public IPurgeStrategy Resolve(RetentionStrategy strategy) =>
        _strategies.TryGetValue(strategy, out var result)
            ? result
            : throw new InvalidOperationException(
                $"Retention strategy '{strategy}' non configurata.");
}
