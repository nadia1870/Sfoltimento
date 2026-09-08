using System.Data;
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
public sealed class PurgeRunStore(ISqlExecutor sql)
{
    public async Task<Guid> CreateAsync(RetentionStrategy strategy, PurgeOptions options,
                                        DateTimeOffset reference, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        const string insert = """
            INSERT INTO Purge.PurgeRun
                (RunId, Strategy, Phase, DryRun, AnchorMode, RetentionCutoff,
                 AbandonedCutoff, MaxRowsPerBatch, MaxOrdersPerBatch, StartedOn,
                 PolicyHash)
            VALUES
                (@RunId, @Strategy, 'Created', @DryRun, @AnchorMode, @Cutoff,
                 @AbandonedCutoff, @MaxRows, @MaxOrders, SYSDATETIMEOFFSET(),
                 @PolicyHash);
            """;

        await sql.ExecuteAsync(insert, ct,
            SqlParam.Of("@RunId", runId),
            SqlParam.Of("@Strategy", strategy.ToString()),
            SqlParam.Of("@DryRun", options.DryRun),
            SqlParam.Of("@AnchorMode", options.AnchorMode.ToString()),
            SqlParam.Typed("@Cutoff", options.ComputeRetentionCutoff(reference), SqlDbType.DateTime2),
            SqlParam.Typed("@AbandonedCutoff", options.ComputeAbandonedCutoff(reference), SqlDbType.DateTime2),
            SqlParam.Of("@MaxRows", options.MaxRowsPerBatch),
            SqlParam.Of("@MaxOrders", options.MaxOrdersPerBatch),
            // Il run porta con se' la policy sotto cui e' girato: senza,
            // dall'id di un dry-run non si risalirebbe a cosa fu esaminato.
            SqlParam.Of("@PolicyHash", PurgePolicy.ComputeHash(options))).ConfigureAwait(false);

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
        // L'elenco delle fasi terminali viene dall'enum, non riscritto qui:
        // una fase aggiunta al tipo e dimenticata in questa stringa renderebbe
        // riprendibile un run gia' chiuso.
        var q = $"""
            SELECT TOP (1) RunId FROM Purge.PurgeRun
            WHERE Strategy = @Strategy AND Phase NOT IN ({RunPhases.TerminalSqlList})
            ORDER BY StartedOn;
            """;
        var id = await sql.ScalarAsync<Guid>(q, ct,
            SqlParam.Of("@Strategy", strategy.ToString())).ConfigureAwait(false);
        return id == Guid.Empty ? null : id;
    }

    /// <summary>
    /// Forma delle righe di Purge.PurgeRun come le legge Dapper: i nomi sono
    /// quelli delle colonne, gli enum arrivano come stringa e vengono
    /// convertiti qui, cosi' un valore sconosciuto fallisce con il nome del
    /// run e non con un errore di mapping.
    /// </summary>
    private sealed record PurgeRunRow(
        Guid RunId, string Strategy, string Phase, bool DryRun, string AnchorMode,
        DateTime RetentionCutoff, DateTime? AbandonedCutoff,
        int MaxRowsPerBatch, int MaxOrdersPerBatch, int InterruptionCount)
    {
        public PurgeRun ToDomain() => new()
        {
            RunId = RunId,
            Strategy = Enum.Parse<RetentionStrategy>(Strategy),
            Phase = Enum.Parse<RunPhase>(Phase),
            DryRun = DryRun,
            AnchorMode = Enum.Parse<RetentionAnchorMode>(AnchorMode),
            RetentionCutoff = RetentionCutoff,
            AbandonedCutoff = AbandonedCutoff,
            MaxRowsPerBatch = MaxRowsPerBatch,
            MaxOrdersPerBatch = MaxOrdersPerBatch,
            InterruptionCount = InterruptionCount
        };
    }

    public async Task<PurgeRun> LoadAsync(Guid runId, CancellationToken ct)
    {
        const string q = """
            SELECT RunId, Strategy, Phase, DryRun, AnchorMode, RetentionCutoff,
                   AbandonedCutoff, MaxRowsPerBatch, MaxOrdersPerBatch,
                   InterruptionCount
            FROM Purge.PurgeRun WHERE RunId = @RunId;
            """;

        var rows = await sql.QueryAsync<PurgeRunRow>(q, ct, SqlParam.Of("@RunId", runId))
            .ConfigureAwait(false);

        return rows.Count == 0
            ? throw new InvalidOperationException($"Run {runId} inesistente.")
            : rows[0].ToDomain();
    }

    public Task SetPhaseAsync(Guid runId, RunPhase phase, CancellationToken ct, string? error = null)
    {
        const string u = """
            UPDATE Purge.PurgeRun
               SET Phase = @Phase,
                   LastError = COALESCE(@Error, LastError),
                   CompletedOn = CASE WHEN @Phase IN ('Completed','CompletedWithErrors','Failed')
                                      THEN SYSDATETIMEOFFSET() ELSE CompletedOn END,
                   -- Un cambio di fase e' progresso: il run ha portato a
                   -- termine qualcosa, e le interruzioni accumulate prima non
                   -- dicono piu' niente. Sta nella stessa UPDATE perche' una
                   -- seconda scrittura per la stessa informazione sarebbe un
                   -- round-trip regalato.
                   InterruptionCount = 0
             WHERE RunId = @RunId;
            """;
        return sql.ExecuteAsync(u, ct,
            SqlParam.Of("@RunId", runId),
            SqlParam.Of("@Phase", phase.ToString()),
            SqlParam.Of("@Error", error));
    }

    private sealed record SliceRow(
        int BatchNo, int OrderCount, int EstimatedRowCount, int AttemptCount,
        bool IsOversized, int SplitDepth)
    {
        public SliceInfo ToDomain() => new()
        {
            BatchNo = BatchNo,
            OrderCount = OrderCount,
            EstimatedRowCount = EstimatedRowCount,
            AttemptCount = AttemptCount,
            IsOversized = IsOversized,
            SplitDepth = SplitDepth
        };
    }

    public async Task<SliceInfo?> NextPendingSliceAsync(Guid runId, CancellationToken ct)
    {
        var rows = await sql.QueryAsync<SliceRow>(RetentionSql.NextPendingSlice, ct,
            SqlParam.Of("@RunId", runId)).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0].ToDomain();
    }

    /// <summary>
    /// L'approvazione per la policy indicata, o null se nessuno ha mai
    /// esaminato un dry-run prodotto con questa configurazione su questo
    /// database.
    /// </summary>
    public async Task<PolicyApproval?> FindApprovalAsync(string policyHash, CancellationToken ct)
    {
        // PolicyApproval e' gia' un record con i nomi delle colonne.
        var righe = await sql.QueryAsync<PolicyApproval>("""
            SELECT PolicyHash, DryRunRunId, ApprovedOn, ApprovedBy, Note
            FROM Purge.PolicyApproval WHERE PolicyHash = @Hash;
            """, ct, SqlParam.Of("@Hash", policyHash)).ConfigureAwait(false);

        return righe.SingleOrDefault();
    }

    /// <summary>Modalita', esito e policy di un run: serve al comando di approvazione.</summary>
    private sealed record PolicyRow(bool DryRun, string Phase, string? PolicyHash);

    public async Task<(bool DryRun, RunPhase Phase, string? PolicyHash)?> ReadPolicyAsync(
        Guid runId, CancellationToken ct)
    {
        var righe = await sql.QueryAsync<PolicyRow>("""
            SELECT DryRun, Phase, PolicyHash FROM Purge.PurgeRun WHERE RunId = @RunId;
            """, ct, SqlParam.Of("@RunId", runId)).ConfigureAwait(false);

        return righe.Count == 0
            ? null
            : (righe[0].DryRun, Enum.Parse<RunPhase>(righe[0].Phase), righe[0].PolicyHash);
    }

    /// <summary>
    /// Registra un'approvazione. Riapprovare la stessa policy sovrascrive:
    /// chi l'ha esaminata per ultimo e' chi ne risponde.
    /// </summary>
    public Task ApproveAsync(
        string policyHash, Guid dryRunRunId, string approvedBy,
        string policyText, string? note, CancellationToken ct) =>
        sql.ExecuteAsync("""
            MERGE Purge.PolicyApproval AS t
            USING (SELECT @Hash AS PolicyHash) AS s ON t.PolicyHash = s.PolicyHash
            WHEN MATCHED THEN UPDATE SET
                DryRunRunId = @RunId, ApprovedOn = SYSDATETIMEOFFSET(),
                ApprovedBy = @By, Note = @Note, PolicyText = @Text
            WHEN NOT MATCHED THEN INSERT
                (PolicyHash, DryRunRunId, ApprovedOn, ApprovedBy, PolicyText, Note)
                VALUES (@Hash, @RunId, SYSDATETIMEOFFSET(), @By, @Text, @Note);
            """, ct,
            SqlParam.Of("@Hash", policyHash), SqlParam.Of("@RunId", dryRunRunId),
            SqlParam.Of("@By", approvedBy), SqlParam.Of("@Text", policyText),
            SqlParam.Of("@Note", note));

    /// <summary>
    /// Azzera il contatore delle interruzioni.
    ///
    /// Serve per il progresso che non cambia fase, cioe' le slice completate
    /// durante Executing: quella e' la fase lunga, e un run che macina slice
    /// per ore senza transizioni non azzererebbe mai.
    ///
    /// Condizionata a un contatore diverso da zero, cosi' nel caso normale —
    /// che e' la stragrande maggioranza — non scrive niente.
    /// </summary>
    public Task ResetInterruptionsAsync(Guid runId, CancellationToken ct) =>
        sql.ExecuteAsync("""
            UPDATE Purge.PurgeRun
               SET InterruptionCount = 0
             WHERE RunId = @RunId AND InterruptionCount > 0;
            """, ct, SqlParam.Of("@RunId", runId));

    /// <summary>
    /// Registra un'interruzione da guasto senza toccare la fase.
    ///
    /// La fase non cambia perche' e' il checkpoint da cui il run riprende. Il
    /// contatore esiste solo per accorgersi di un guasto che non passa: oltre
    /// la soglia l'orchestratore smette di riprovare.
    /// </summary>
    public Task RecordInterruptionAsync(Guid runId, string? error, CancellationToken ct)
    {
        const string u = """
            UPDATE Purge.PurgeRun
               SET InterruptionCount = InterruptionCount + 1,
                   LastInterruptedOn = SYSDATETIMEOFFSET(),
                   LastError = COALESCE(@Error, LastError)
             WHERE RunId = @RunId;
            """;
        return sql.ExecuteAsync(u, ct,
            SqlParam.Of("@RunId", runId), SqlParam.Of("@Error", error));
    }

    /// <summary>
    /// Slice abbandonate del run, lette dal database e non da un contatore in
    /// memoria: un run ripreso su piu' notti abbandona in una sessione e
    /// completa in un'altra, e il contatore dell'ultima sessione direbbe zero.
    /// </summary>
    public async Task<int> CountAbandonedSlicesAsync(Guid runId, CancellationToken ct) =>
        await sql.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Purge.RunBatchProgress
            WHERE RunId = @RunId AND Status = 'Abandoned';
            """, ct, SqlParam.Of("@RunId", runId)).ConfigureAwait(false);

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

    /// <summary>
    /// Divide una slice in due figlie per aggregato (D-11). Restituisce quante
    /// figlie ha creato: zero se la slice conteneva un aggregato solo, e in
    /// quel caso non ha toccato niente e il chiamante deve abbandonare.
    /// </summary>
    public async Task<int> SplitSliceAsync(Guid runId, int batchNo, string? reason, CancellationToken ct) =>
        await sql.ScalarAsync<int>(RetentionSql.SplitSlice, ct,
            SqlParam.Of("@RunId", runId),
            SqlParam.Of("@BatchNo", batchNo),
            SqlParam.Of("@Reason", reason)).ConfigureAwait(false);

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

    // Purge.PurgeAudit non si scrive da qui. La scrittura sta in
    // SliceExecutor, dentro la transazione della slice: un secondo percorso
    // fuori transazione potrebbe registrare cancellazioni poi annullate dal
    // rollback, che e' esattamente l'errore che l'audit dovrebbe escludere.
}
