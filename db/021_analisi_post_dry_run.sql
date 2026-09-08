/* =====================================================================
   021 — Analisi DOPO il dry-run

   Sola lettura sullo schema Purge (piu' qualche join su PaymentOrder per
   dare un contesto ai candidati). Legge l'ULTIMO run di ogni strategia,
   dry-run o reale, e produce cio' che serve per decidere se approvare.

   Da eseguire finche' lo staging esiste: PurgeRun, DryRunReport e
   ValidationFinding restano per sempre, RunCandidate* viene rimosso
   dall'housekeeping dopo StagingRetentionDays (default 7). Le sezioni
   D-G hanno bisogno dello staging.
   ===================================================================== */

SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

/* Se serve guardare un run preciso invece dell'ultimo per strategia,
   valorizzare @SoloRunId. */
DECLARE @SoloRunId UNIQUEIDENTIFIER = NULL;

DECLARE @run TABLE (RunId UNIQUEIDENTIFIER PRIMARY KEY, Strategy VARCHAR(30), DryRun BIT,
                    Phase VARCHAR(30), StartedOn DATETIMEOFFSET);

INSERT INTO @run
SELECT r.RunId, r.Strategy, r.DryRun, r.Phase, r.StartedOn
FROM Purge.PurgeRun r
WHERE (@SoloRunId IS NOT NULL AND r.RunId = @SoloRunId)
   OR (@SoloRunId IS NULL AND r.StartedOn = (SELECT MAX(x.StartedOn) FROM Purge.PurgeRun x
                                             WHERE x.Strategy = r.Strategy));

/* =====================================================================
   A. ESITO — un run per strategia
   ===================================================================== */
SELECT Sezione = 'A. Esito',
       r.Strategy, r.RunId, r.DryRun, r.Phase,
       Esito = CASE r.Phase
                 WHEN 'Completed' THEN 'OK'
                 WHEN 'CompletedWithErrors' THEN 'OK con slice abbandonate'
                 WHEN 'Failed' THEN 'FALLITO: vedere LastError e sezione C'
                 ELSE 'NON CONCLUSO (riprende al prossimo lancio)' END,
       p.AnchorMode, p.RetentionCutoff, p.AbandonedCutoff,
       p.MaxRowsPerBatch, p.MaxOrdersPerBatch,
       p.StartedOn, p.CompletedOn,
       DurataMin = DATEDIFF(MINUTE, p.StartedOn, ISNULL(p.CompletedOn, SYSDATETIMEOFFSET())),
       p.InterruptionCount, p.LastInterruptedOn,
       p.PolicyHash,
       Approvata = CASE WHEN EXISTS (SELECT 1 FROM Purge.PolicyApproval a WHERE a.PolicyHash = p.PolicyHash)
                        THEN 'si' ELSE 'no' END,
       p.LastError
FROM @run r
JOIN Purge.PurgeRun p ON p.RunId = r.RunId
ORDER BY r.Strategy;

/* =====================================================================
   B. REPORT PREVISIONALE — righe per tabella, come lo vede chi approva
   ===================================================================== */
SELECT Sezione = 'B. Report',
       r.Strategy, d.TableName, Righe = d.RowCountEstimate, d.ProducedOn
FROM @run r
JOIN Purge.DryRunReport d ON d.RunId = r.RunId
WHERE d.RowCountEstimate > 0
ORDER BY r.Strategy, d.RowCountEstimate DESC;

SELECT Sezione = 'B. Totali',
       r.Strategy,
       Ordini = SUM(CASE WHEN d.TableName = 'Order' THEN d.RowCountEstimate ELSE 0 END),
       Storici = SUM(CASE WHEN d.TableName = 'OrderHistory' THEN d.RowCountEstimate ELSE 0 END),
       Collettivi = SUM(CASE WHEN d.TableName = 'CollectiveOrder' THEN d.RowCountEstimate ELSE 0 END),
       RigheTotali = SUM(d.RowCountEstimate)
FROM @run r
LEFT JOIN Purge.DryRunReport d ON d.RunId = r.RunId
GROUP BY r.Strategy
ORDER BY r.Strategy;

/* =====================================================================
   C. VALIDAZIONI — qualunque riga qui ha fatto fallire il run
   V1 cross-reference (C7) | V2 modelli (C5) | V3 legame collettivo
   V4 stato cambiato       | V5 copertura storici
   ===================================================================== */
SELECT Sezione = 'C. Validazioni',
       r.Strategy, v.RuleId, v.TableName, v.AffectedCount, v.DetectedOn
FROM @run r
JOIN Purge.ValidationFinding v ON v.RunId = r.RunId
ORDER BY r.Strategy, v.RuleId;

/* =====================================================================
   D. COLLETTIVI — selezionati, esclusi e perche' (D-10, PA-7)
   ===================================================================== */
SELECT Sezione = 'D. Collettivi',
       r.Strategy, c.State, Motivo = ISNULL(c.ExcludedReason, ''),
       Collettivi = COUNT(*), Componenti = SUM(c.OrderCount)
FROM @run r
JOIN Purge.RunCandidateCollective c ON c.RunId = r.RunId
GROUP BY r.Strategy, c.State, ISNULL(c.ExcludedReason, '')
ORDER BY r.Strategy, c.State, Collettivi DESC;

/* Elenco dei collettivi esclusi, per il referente applicativo. */
SELECT TOP (200) Sezione = 'D. Collettivi esclusi (elenco)',
       r.Strategy, c.CollectiveOrderId, c.ExcludedReason,
       co.StatusCode, co.ExecutionDate,
       Componenti = (SELECT COUNT(*) FROM PaymentOrder.CollectiveOrderGroup g
                     JOIN PaymentOrder.CollectiveOrderGroupOrder x ON x.CollectiveOrderGroupId = g.Id
                     WHERE g.CollectiveOrderId = c.CollectiveOrderId AND x.OrderId IS NOT NULL)
FROM @run r
JOIN Purge.RunCandidateCollective c ON c.RunId = r.RunId
LEFT JOIN PaymentOrder.CollectiveOrder co ON co.Id = c.CollectiveOrderId
WHERE c.State = 'Excluded'
ORDER BY c.ExcludedReason, co.ExecutionDate;

/* =====================================================================
   E. SLICE — quante, quanto pesano, quante oversized
   In dry-run RunBatchProgress non esiste: si legge dalle assegnazioni.
   ===================================================================== */
SELECT Sezione = 'E. Statistiche slice',
       r.Strategy, r.Phase,
       Slice = COUNT(*),
       RigheMin = MIN(s.Righe), RigheMedia = AVG(s.Righe), RigheMax = MAX(s.Righe),
       OrdiniMin = MIN(s.Ordini), OrdiniMedia = AVG(s.Ordini), OrdiniMax = MAX(s.Ordini),
       Oversized = SUM(s.IsOversized),
       Note = CASE WHEN MAX(s.Righe) > MAX(p.MaxRowsPerBatch) AND SUM(s.IsOversized) = 0
                   THEN 'ATTENZIONE: slice oltre il tetto senza oversized — packing da rivedere'
                   ELSE '' END
FROM @run r
JOIN Purge.PurgeRun p ON p.RunId = r.RunId
JOIN (
    SELECT RunId, BatchNo,
           Righe = SUM(ISNULL(RowWeight, 1)), Ordini = COUNT(*),
           IsOversized = MAX(CAST(IsOversized AS INT))
    FROM Purge.RunCandidateOrder
    WHERE BatchNo IS NOT NULL
    GROUP BY RunId, BatchNo
    UNION ALL
    SELECT RunId, BatchNo, Righe = COUNT(*), Ordini = COUNT(*), IsOversized = 0
    FROM Purge.RunCandidateOrderHistory
    WHERE OrderId IS NULL AND BatchNo IS NOT NULL
    GROUP BY RunId, BatchNo
) s ON s.RunId = r.RunId
GROUP BY r.Strategy, r.Phase
ORDER BY r.Strategy;

/* Stima della durata dell'esecuzione reale: il dry-run non la misura.
   1-3 secondi per slice e' un intervallo prudente; il valore reale si legge
   da purge.slice_duration dopo la prima notte. */
SELECT Sezione = 'E. Durata stimata',
       r.Strategy,
       Slice = COUNT(*),
       OreA1s = CAST(COUNT(*) * 1.1 / 3600.0 AS DECIMAL(8,1)),
       OreA3s = CAST(COUNT(*) * 3.1 / 3600.0 AS DECIMAL(8,1)),
       NottiA3s_Finestra4h = CEILING(COUNT(*) * 3.1 / 3600.0 / 4.0)
FROM @run r
JOIN (SELECT RunId, BatchNo FROM Purge.RunCandidateOrder WHERE BatchNo IS NOT NULL GROUP BY RunId, BatchNo
      UNION ALL
      SELECT RunId, BatchNo FROM Purge.RunCandidateOrderHistory WHERE OrderId IS NULL AND BatchNo IS NOT NULL GROUP BY RunId, BatchNo) s
  ON s.RunId = r.RunId
GROUP BY r.Strategy;

/* Aggregati oversized: transazione dedicata ciascuno. */
SELECT TOP (50) Sezione = 'E. Oversized (elenco)',
       r.Strategy, c.OrderId, c.CollectiveOrderId, c.RowWeight, c.BatchNo,
       o.StatusCode, o.ExecutionDate
FROM @run r
JOIN Purge.RunCandidateOrder c ON c.RunId = r.RunId AND c.IsOversized = 1
LEFT JOIN PaymentOrder.[Order] o ON o.Id = c.OrderId
ORDER BY c.RowWeight DESC;

/* Ordini selezionati senza slice: deve essere zero. */
SELECT Sezione = 'E. Senza BatchNo',
       r.Strategy, Ordini = COUNT(*)
FROM @run r
JOIN Purge.RunCandidateOrder c ON c.RunId = r.RunId
WHERE c.State = 'Selected' AND c.BatchNo IS NULL
GROUP BY r.Strategy;

/* =====================================================================
   F. CANDIDATI — chi sono
   ===================================================================== */

/* F1. Per anno di esecuzione: non deve comparire nessun anno oltre la soglia. */
SELECT Sezione = 'F1. Candidati per anno',
       r.Strategy, Anno = YEAR(o.ExecutionDate),
       Ordini = COUNT(*), Righe = SUM(ISNULL(c.RowWeight, 1)),
       -- Solo per Terminated e Collective: StandingOrders ancora su
       -- LastExecutionDate, quindi ExecutionDate puo' legittimamente superare la soglia.
       OltreSoglia = CASE WHEN r.Strategy IN ('Terminated', 'Collective')
                           AND MAX(o.ExecutionDate) >= MAX(p.RetentionCutoff) THEN 'ATTENZIONE' ELSE '' END
FROM @run r
JOIN Purge.PurgeRun p ON p.RunId = r.RunId
JOIN Purge.RunCandidateOrder c ON c.RunId = r.RunId
JOIN PaymentOrder.[Order] o ON o.Id = c.OrderId
GROUP BY r.Strategy, YEAR(o.ExecutionDate)
ORDER BY r.Strategy, Anno;

/* F2. Per stato: solo stati terminali. */
SELECT Sezione = 'F2. Candidati per stato',
       r.Strategy, o.StatusCode, Ordini = COUNT(*)
FROM @run r
JOIN Purge.RunCandidateOrder c ON c.RunId = r.RunId
JOIN PaymentOrder.[Order] o ON o.Id = c.OrderId
GROUP BY r.Strategy, o.StatusCode
ORDER BY r.Strategy, Ordini DESC;

/* F3. Per tipo di dettaglio: quale tabella porta il volume. */
SELECT Sezione = 'F3. Candidati per tipo',
       r.Strategy, Tipo = t.Tipo, Ordini = t.Ordini
FROM @run r
CROSS APPLY (
    SELECT 'BankTransfer' AS Tipo, COUNT(*) AS Ordini FROM Purge.RunCandidateOrder c JOIN PaymentOrder.BankTransfer d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'AccountTransfer', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.AccountTransfer d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'ForeignBankTransfer', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.ForeignBankTransfer d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'QRBill', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.QRBill d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'IpQRBill', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.IpQRBill d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'IpBankTransfer', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.IpBankTransfer d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'InpaymentSlip', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.InpaymentSlip d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'BankTransferToCornerCard', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.BankTransferToCornerCard d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'RealTimeCardReload', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.RealTimeCardReload d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
    UNION ALL SELECT 'StandingOrder', COUNT(*) FROM Purge.RunCandidateOrder c JOIN PaymentOrder.StandingOrder d ON d.OrderId = c.OrderId WHERE c.RunId = r.RunId
) t
WHERE t.Ordini > 0
ORDER BY r.Strategy, t.Ordini DESC;

/* F4. Peso: distribuzione reale sui candidati, da confrontare con 020 §3. */
SELECT Sezione = 'F4. Pesi',
       r.Strategy,
       Ordini = COUNT(*),
       PesoMedio = AVG(ISNULL(c.RowWeight, 1) * 1.0),
       PesoMax = MAX(c.RowWeight),
       OltreTetto = SUM(CASE WHEN c.RowWeight > p.MaxRowsPerBatch THEN 1 ELSE 0 END)
FROM @run r
JOIN Purge.PurgeRun p ON p.RunId = r.RunId
JOIN Purge.RunCandidateOrder c ON c.RunId = r.RunId
GROUP BY r.Strategy;

/* F5. Campione: dieci candidati a caso per un controllo a mano. */
SELECT TOP (10) Sezione = 'F5. Campione',
       r.Strategy, c.OrderId, o.Code, o.StatusCode, o.ExecutionDate, o.CreationDate,
       Revisioni = (c.RowWeight - 1) / 2
FROM @run r
JOIN Purge.RunCandidateOrder c ON c.RunId = r.RunId
JOIN PaymentOrder.[Order] o ON o.Id = c.OrderId
WHERE r.Strategy = 'Terminated'
ORDER BY NEWID();

/* =====================================================================
   G. STAGING — quanto occupa, per pianificare l'housekeeping
   ===================================================================== */
SELECT Sezione = 'G. Staging',
       Tabella = QUOTENAME(s.name) + '.' + QUOTENAME(t.name),
       Righe = SUM(CASE WHEN i.index_id IN (0,1) THEN p.rows ELSE 0 END),
       MB = CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(10,1))
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.indexes i ON i.object_id = t.object_id
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
WHERE s.name = 'Purge'
GROUP BY s.name, t.name
ORDER BY MB DESC;

SELECT Sezione = 'G. Run con staging',
       Strategy, Phase, Run_ = COUNT(*), PiuVecchio = MIN(StartedOn)
FROM Purge.PurgeRun
WHERE StagingPurgedOn IS NULL
GROUP BY Strategy, Phase
ORDER BY Strategy, Phase;

/* =====================================================================
   H. SOLO DOPO UN RUN REALE — audit, abbandoni, bisezioni
   Vuoto dopo un dry-run. Vedi anche 009_verify_audit.sql.
   ===================================================================== */
SELECT Sezione = 'H. Slice per stato',
       r.Strategy, b.Status, Slice = COUNT(*), Ordini = SUM(b.OrderCount),
       RigheCancellate = SUM(b.ActualDeletedRows),
       Tentativi = SUM(b.AttemptCount),
       ProfonditaMax = MAX(b.SplitDepth)
FROM @run r
JOIN Purge.RunBatchProgress b ON b.RunId = r.RunId
GROUP BY r.Strategy, b.Status
ORDER BY r.Strategy, b.Status;

/* Aggregati abbandonati: dopo la bisezione ognuno e' un aggregato solo,
   e LastError dice perche'. */
SELECT TOP (100) Sezione = 'H. Abbandonati',
       r.Strategy, b.BatchNo, b.ParentBatchNo, b.SplitDepth, b.OrderCount, b.LastError, b.CompletedOn,
       OrderId = (SELECT TOP (1) c.OrderId FROM Purge.RunCandidateOrder c WHERE c.RunId = b.RunId AND c.BatchNo = b.BatchNo),
       CollectiveOrderId = (SELECT TOP (1) c.CollectiveOrderId FROM Purge.RunCandidateOrder c WHERE c.RunId = b.RunId AND c.BatchNo = b.BatchNo)
FROM @run r
JOIN Purge.RunBatchProgress b ON b.RunId = r.RunId
WHERE b.Status = 'Abandoned'
ORDER BY r.Strategy, b.BatchNo;

/* Previsto vs effettivo, per tabella. */
SELECT Sezione = 'H. Previsto vs effettivo',
       r.Strategy, v.TableName, v.Previsto, v.Effettivo, v.Scostamento
FROM @run r
JOIN Purge.vDryRunVsActual v ON v.RunId = r.RunId
WHERE r.DryRun = 0 AND (v.Previsto > 0 OR v.Effettivo > 0)
ORDER BY r.Strategy, ABS(v.Scostamento) DESC;
