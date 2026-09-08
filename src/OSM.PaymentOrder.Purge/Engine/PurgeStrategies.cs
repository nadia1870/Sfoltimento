using System.Data;
using Microsoft.Extensions.Logging;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Observability;
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
///
/// La selezione procede in tre tempi (D-10): eleggibili, esclusioni per
/// motivo, componenti. Le esclusioni censiscono in RunCandidateCollective i
/// collettivi che non si possono cancellare senza far fallire il run:
/// prima arrivavano in Validating, che e' fail-hard, e un solo collettivo
/// anomalo bloccava l'intera strategia notte dopo notte.
/// </summary>
public sealed class CollectiveStrategy(
    ISqlExecutor sql,
    BatchedStatementRunner batched,
    PurgeMetrics metrics,
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

    /// <summary>
    /// Le esclusioni, nell'ordine in cui vengono applicate. Statiche perche'
    /// derivano dalla topologia: una tabella di storico aggiunta a
    /// PurgeTopology compare qui da sola.
    /// </summary>
    public static IEnumerable<(string Reason, string Sql)> ExclusionStatements()
    {
        yield return ("ComponentHasModel", RetentionSql.ExcludeCollectivesWithModel);
        yield return ("AmbiguousMembership", RetentionSql.ExcludeAmbiguousCollectives);

        foreach (var t in PurgeTopology.DetailHistoryTables)
        {
            yield return (RetentionSql.CrossReferenceReasonPrefix + t.Name,
                          RetentionSql.ExcludeCollectivesWithCrossReference(t));
        }
    }

    public override async Task<int> SelectAsync(PurgeRun run, CancellationToken ct)
    {
        var p = new[]
        {
            SqlParam.Of("@RunId", run.RunId),
            SqlParam.Typed("@Cutoff", CutoffOf(run), SqlDbType.DateTime2)
        };
        var runP = SqlParam.Of("@RunId", run.RunId);

        var eligible = await _sql.ExecuteAsync(RetentionSql.SelectEligibleCollectives, ct, p)
            .ConfigureAwait(false);

        // Manteniamo il censimento degli anomalie della V3: e' una scrittura
        // intenzionale in Purge.RunCandidateCollective e non va persa nel refactoring.
        var withoutDate = await _sql.ExecuteAsync(
            RetentionSql.SelectCollectivesWithoutDate, ct, runP).ConfigureAwait(false);

        if (withoutDate > 0)
        {
            log.LogWarning(
                "RunId={RunId}: {Count} collettivi privi di ExecutionDate, esclusi e censiti.",
                run.RunId, withoutDate);
        }

        // Esclusioni per motivo. Ogni UPDATE restituisce quanti collettivi ha
        // spostato in 'Excluded' in questo giro: alla riesecuzione dopo
        // un'interruzione sono gia' esclusi e il conteggio e' zero, quindi il
        // log riflette la selezione corrente e non lo stato accumulato.
        var excluded = 0;
        foreach (var (reason, statement) in ExclusionStatements())
        {
            ct.ThrowIfCancellationRequested();
            var n = await _sql.ExecuteAsync(statement, ct, runP).ConfigureAwait(false);
            if (n == 0) continue;

            excluded += n;
            metrics.CandidatesExcluded(reason, n);
            log.LogWarning(
                "PurgeCollectiveExcluded RunId={RunId} Motivo={Reason} Collettivi={Count} — " +
                "censiti in RunCandidateCollective, non verranno cancellati.",
                run.RunId, reason, n);
        }

        // Post-condizione, non piu' controllo di dominio: ExcludeAmbiguousCollectives
        // ha appena rimosso le coppie. Un residuo qui e' un difetto di quella
        // UPDATE e deve fermare il run, non essere gestito.
        var ambiguousOrders = await _sql.ScalarAsync<long>(
            RetentionSql.ValidateOrderBelongsToSingleCollective, ct, runP).ConfigureAwait(false);

        if (ambiguousOrders > 0)
            throw new InvalidOperationException(
                $"Run {run.RunId}: {ambiguousOrders} ordini appartengono ancora a piu' Collective " +
                "selezionati dopo l'esclusione delle appartenenze ambigue: difetto in " +
                "ExcludeAmbiguousCollectives.");

        var components = await _sql.ExecuteAsync(
            RetentionSql.SelectCollectiveComponents, ct, runP).ConfigureAwait(false);

        log.LogInformation(
            "RunId={RunId} Strategy={Strategy} CollettiviEleggibili={Eligible} Esclusi={Excluded} " +
            "Componenti={Components}",
            run.RunId, Type, eligible, excluded, components);

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
