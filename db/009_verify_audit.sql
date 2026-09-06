/* =====================================================================
   Verifica della traccia di audit — da eseguire dopo un run reale

   Tre controlli indipendenti. Il primo e' quello che conta: se l'audit e
   il checkpoint non concordano, uno dei due sta mentendo e nessuno dei
   due e' utilizzabile come traccia.
   ===================================================================== */

SET NOCOUNT ON;
GO

/* --- 1. L'audit quadra con il checkpoint, run per run ------------------
   ActualDeletedRows e' scritto da CheckpointSlice, le righe di audit dai
   rowcount dei singoli statement, entrambi nella stessa transazione.
   Uno scostamento qui indica uno statement di slice che cancella senza
   essere tracciato, o viceversa.                                        */
SELECT
    r.RunId,
    r.Strategy,
    r.Phase,
    Checkpoint = ISNULL(b.Righe, 0),
    Audit      = ISNULL(a.Righe, 0),
    Esito      = CASE WHEN ISNULL(b.Righe, 0) = ISNULL(a.Righe, 0)
                      THEN 'OK' ELSE 'DIVERGENTE' END
FROM Purge.PurgeRun AS r
OUTER APPLY (SELECT Righe = SUM(CAST(ActualDeletedRows AS BIGINT))
             FROM Purge.RunBatchProgress
             WHERE RunId = r.RunId AND Status = 'Completed') AS b
OUTER APPLY (SELECT Righe = SUM(RowsDeleted)
             FROM Purge.PurgeAudit WHERE RunId = r.RunId) AS a
WHERE r.DryRun = 0
ORDER BY r.StartedOn DESC;
GO

/* --- 2. Ogni riga di audit riferisce una slice conclusa ---------------- */
SELECT
    RigheOrfane = COUNT_BIG(*),
    Esito       = CASE WHEN COUNT_BIG(*) = 0 THEN 'OK' ELSE 'DA ANALIZZARE' END
FROM Purge.PurgeAudit AS a
WHERE NOT EXISTS (SELECT 1 FROM Purge.RunBatchProgress AS b
                  WHERE b.RunId = a.RunId AND b.BatchNo = a.BatchNo
                    AND b.Status = 'Completed');
GO

/* --- 3. Previsto contro effettivo -------------------------------------
   Righe con scostamento diverso da zero sui run reali. Un valore negativo
   significa che e' stato cancellato meno del previsto: tipicamente slice
   abbandonate, o ordini usciti dallo stato terminale fra la pianificazione
   e l'esecuzione. Un valore positivo non dovrebbe mai comparire e va
   trattato come un difetto.                                             */
SELECT
    v.RunId,
    r.Strategy,
    v.TableName,
    v.Previsto,
    v.Effettivo,
    v.Scostamento
FROM Purge.vDryRunVsActual AS v
JOIN Purge.PurgeRun AS r ON r.RunId = v.RunId
WHERE r.DryRun = 0 AND v.Scostamento <> 0
ORDER BY r.StartedOn DESC, ABS(v.Scostamento) DESC;
GO

/* --- 4. Dettaglio per tabella dell'ultimo run reale -------------------- */
SELECT TOP (1) WITH TIES
    r.RunId, r.Strategy, r.StartedOn, r.CompletedOn
INTO #ultimo
FROM Purge.PurgeRun AS r
WHERE r.DryRun = 0 AND r.Phase = 'Completed'
ORDER BY r.StartedOn DESC;

SELECT
    u.Strategy,
    a.TableName,
    Righe = SUM(a.RowsDeleted),
    Slice = COUNT(DISTINCT a.BatchNo)
FROM Purge.PurgeAudit AS a
JOIN #ultimo AS u ON u.RunId = a.RunId
GROUP BY u.Strategy, a.TableName
ORDER BY Righe DESC;

DROP TABLE #ultimo;
GO
