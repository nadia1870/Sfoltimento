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

    /// <summary>La strategy richiede il cleanup finale del collective.</summary>
    bool RequiresCollectiveTail { get; }

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
    SqlExecutor sql,
    ILogger log) : IPurgeStrategy
{
    public abstract RetentionStrategy Type { get; }
    public virtual PurgePlanningMode PlanningMode => PurgePlanningMode.Standard;
    public virtual bool RequiresCollectiveTail => false;
    public virtual bool SkipCollectiveLinkValidation => false;
    public virtual bool UsesAbandonedDeletes => false;

    public virtual DateTime CutoffOf(PurgeRun run) => run.RetentionCutoff;

    public abstract Task<int> SelectAsync(PurgeRun run, CancellationToken ct);

    public virtual async Task ExpandAsync(PurgeRun run, CancellationToken ct)
    {
        var histories = await sql.ExecuteAsync(
            RetentionSql.ExpandOrderHistory,
            ct,
            SqlParam.Of("@RunId", run.RunId)).ConfigureAwait(false);

        await sql.ExecuteAsync(
            RetentionSql.ComputeWeights,
            ct,
            SqlParam.Of("@RunId", run.RunId)).ConfigureAwait(false);

        log.LogInformation(
            "PurgeExpansionCompleted RunId={RunId} Strategy={Strategy} Storici={Histories}",
            run.RunId, Type, histories);
    }

    public virtual IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
        RetentionSql.SliceStatements(UsesAbandonedDeletes);

    protected async Task<int> ExecuteSelectionAsync(
        string statement,
        PurgeRun run,
        CancellationToken ct) =>
        await sql.ExecuteAsync(
            statement,
            ct,
            SqlParam.Of("@RunId", run.RunId),
            SqlParam.Of("@Cutoff", CutoffOf(run))).ConfigureAwait(false);
}

public sealed class TerminatedStrategy(SqlExecutor sql, ILogger<TerminatedStrategy> log)
    : PurgeStrategyBase(sql, log)
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

public sealed class AbandonedStrategy(SqlExecutor sql, ILogger<AbandonedStrategy> log)
    : PurgeStrategyBase(sql, log)
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

public sealed class StandingOrdersStrategy(SqlExecutor sql, ILogger<StandingOrdersStrategy> log)
    : PurgeStrategyBase(sql, log)
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

public sealed class CollectiveStrategy(SqlExecutor sql, ILogger<CollectiveStrategy> log)
    : PurgeStrategyBase(sql, log)
{
    private readonly SqlExecutor _sql = sql;
    public override RetentionStrategy Type => RetentionStrategy.Collective;
    // Il Collective viene eliminato nella stessa transazione dei suoi ordini componenti.
    // Non esiste piu' una delete differita in CollectiveTail.
    public override bool RequiresCollectiveTail => false;
    public override bool SkipCollectiveLinkValidation => true;

    public override IEnumerable<(string Table, string Sql)> GetSliceStatements() =>
        RetentionSql.SliceStatements(abandoned: false)
            .Concat(RetentionSql.CollectiveSliceStatements());

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var p = new[]
        {
            SqlParam.Of("@RunId", run.RunId),
            SqlParam.Of("@Cutoff", CutoffOf(run))
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

public sealed class OrphanHistoryStrategy(SqlExecutor sql, ILogger<OrphanHistoryStrategy> log)
    : PurgeStrategyBase(sql, log)
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
