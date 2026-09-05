using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Engine;

/// <summary>
/// Persistenza del run e dei checkpoint (§7.1, §7.2).
/// Il checkpoint di completamento NON passa da qui: e' scritto da SliceExecutor
/// dentro la transazione della slice, cosi' il progresso registrato non puo'
/// divergere dallo stato reale del database.
/// </summary>
public sealed class PurgeRunStore(SqlExecutor sql)
{
    public async Task<Guid> CreateAsync(RetentionStrategy strategy, PurgeOptions options,
                                        DateTimeOffset reference, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        const string insert = """
            INSERT INTO Purge.PurgeRun
                (RunId, Strategy, Phase, DryRun, AnchorMode, RetentionCutoff,
                 AbandonedCutoff, MaxRowsPerBatch, MaxOrdersPerBatch, StartedOn)
            VALUES
                (@RunId, @Strategy, 'Created', @DryRun, @AnchorMode, @Cutoff,
                 @AbandonedCutoff, @MaxRows, @MaxOrders, SYSDATETIMEOFFSET());
            """;

        await sql.ExecuteAsync(insert, ct,
            SqlParam.Of("@RunId", runId),
            SqlParam.Of("@Strategy", strategy.ToString()),
            SqlParam.Of("@DryRun", options.DryRun),
            SqlParam.Of("@AnchorMode", options.AnchorMode.ToString()),
            SqlParam.Of("@Cutoff", options.ComputeRetentionCutoff(reference)),
            SqlParam.Of("@AbandonedCutoff", options.ComputeAbandonedCutoff(reference)),
            SqlParam.Of("@MaxRows", options.MaxRowsPerBatch),
            SqlParam.Of("@MaxOrders", options.MaxOrdersPerBatch)).ConfigureAwait(false);

        return runId;
    }

    /// <summary>
    /// Riprende un run sospeso oppure ne crea uno nuovo. La ripresa e'
    /// deliberata: il set di candidati resta congelato (§7.2). Ricalcolarlo a
    /// ogni notte renderebbe il progresso non deterministico, perche' un run
    /// lungo opererebbe su insiemi diversi a ogni ripresa.
    /// </summary>
    public async Task<Guid?> FindResumableAsync(RetentionStrategy strategy, CancellationToken ct)
    {
        const string q = """
            SELECT TOP (1) RunId FROM Purge.PurgeRun
            WHERE Strategy = @Strategy AND Phase NOT IN ('Completed','Failed','Aborted')
            ORDER BY StartedOn;
            """;
        var id = await sql.ScalarAsync<Guid>(q, ct,
            SqlParam.Of("@Strategy", strategy.ToString())).ConfigureAwait(false);
        return id == Guid.Empty ? null : id;
    }

    public async Task<PurgeRun> LoadAsync(Guid runId, CancellationToken ct)
    {
        const string q = """
            SELECT RunId, Strategy, Phase, DryRun, AnchorMode, RetentionCutoff,
                   AbandonedCutoff, MaxRowsPerBatch, MaxOrdersPerBatch
            FROM Purge.PurgeRun WHERE RunId = @RunId;
            """;

        var rows = await sql.QueryAsync(q, r => new PurgeRun
        {
            RunId = r.GetGuid(0),
            Strategy = Enum.Parse<RetentionStrategy>(r.GetString(1)),
            Phase = Enum.Parse<RunPhase>(r.GetString(2)),
            DryRun = r.GetBoolean(3),
            AnchorMode = Enum.Parse<RetentionAnchorMode>(r.GetString(4)),
            RetentionCutoff = r.GetDateTime(5),
            AbandonedCutoff = r.IsDBNull(6) ? null : r.GetDateTime(6),
            MaxRowsPerBatch = r.GetInt32(7),
            MaxOrdersPerBatch = r.GetInt32(8)
        }, ct, SqlParam.Of("@RunId", runId)).ConfigureAwait(false);

        return rows.Count == 0
            ? throw new InvalidOperationException($"Run {runId} inesistente.")
            : rows[0];
    }

    public Task SetPhaseAsync(Guid runId, RunPhase phase, CancellationToken ct, string? error = null)
    {
        const string u = """
            UPDATE Purge.PurgeRun
               SET Phase = @Phase,
                   LastError = COALESCE(@Error, LastError),
                   CompletedOn = CASE WHEN @Phase IN ('Completed','Failed')
                                      THEN SYSDATETIMEOFFSET() ELSE CompletedOn END
             WHERE RunId = @RunId;
            """;
        return sql.ExecuteAsync(u, ct,
            SqlParam.Of("@RunId", runId),
            SqlParam.Of("@Phase", phase.ToString()),
            SqlParam.Of("@Error", error));
    }

    public async Task<SliceInfo?> NextPendingSliceAsync(Guid runId, CancellationToken ct)
    {
        var rows = await sql.QueryAsync(RetentionSql.NextPendingSlice, r => new SliceInfo
        {
            BatchNo = r.GetInt32(0),
            OrderCount = r.GetInt32(1),
            EstimatedRowCount = r.GetInt32(2),
            AttemptCount = r.GetInt32(3),
            IsOversized = r.GetBoolean(4)
        }, ct, SqlParam.Of("@RunId", runId)).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    public Task RecordAttemptAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
    {
        const string u = """
            UPDATE Purge.RunBatchProgress
               SET AttemptCount = AttemptCount + 1, Status = 'Running',
                   StartedOn = COALESCE(StartedOn, SYSDATETIMEOFFSET()), LastError = @Reason
             WHERE RunId = @RunId AND BatchNo = @BatchNo;
            """;
        return sql.ExecuteAsync(u, ct, SqlParam.Of("@RunId", runId),
            SqlParam.Of("@BatchNo", batchNo), SqlParam.Of("@Reason", reason));
    }

    public Task AbandonSliceAsync(Guid runId, int batchNo, string? reason, CancellationToken ct)
    {
        const string u = """
            UPDATE Purge.RunBatchProgress
               SET Status = 'Abandoned', LastError = @Reason, CompletedOn = SYSDATETIMEOFFSET()
             WHERE RunId = @RunId AND BatchNo = @BatchNo;

            UPDATE Purge.RunCandidateOrder
               SET State = 'Failed'
             WHERE RunId = @RunId AND BatchNo = @BatchNo AND State = 'Selected';
            """;
        return sql.ExecuteAsync(u, ct, SqlParam.Of("@RunId", runId),
            SqlParam.Of("@BatchNo", batchNo), SqlParam.Of("@Reason", reason));
    }

    public Task RecordAuditAsync(Guid runId, string table, long rows, CancellationToken ct)
    {
        const string i = """
            INSERT INTO Purge.PurgeAudit (RunId, TableName, RowsDeleted, RecordedOn)
            VALUES (@RunId, @Table, @Rows, SYSDATETIMEOFFSET());
            """;
        return sql.ExecuteAsync(i, ct, SqlParam.Of("@RunId", runId),
            SqlParam.Of("@Table", table), SqlParam.Of("@Rows", rows));
    }
}
