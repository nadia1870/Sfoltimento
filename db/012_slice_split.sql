/* =====================================================================
   012 — Bisezione delle slice (D-11)

   Una slice che fallisce per un errore di dati (FK 547, chiave duplicata)
   non viene piu' abbandonata in blocco: viene divisa in due figlie per
   aggregato, che entrano in coda come 'Pending'. La madre resta come
   traccia con Status = 'Split'. La divisione si ripete finche' la slice
   che fallisce contiene un aggregato solo, che e' l'unico che viene
   davvero abbandonato.

   ParentBatchNo permette di ricostruire la genealogia a posteriori:
   "quale aggregato ha ucciso la slice 37" diventa
       WHERE ParentBatchNo = 37 AND Status = 'Abandoned'
   e a quel punto la slice abbandonata contiene un aggregato solo.

   SplitDepth e' il freno: oltre Purge:MaxSplitDepth non si divide piu'.
   Idempotente.
   ===================================================================== */

IF COL_LENGTH('Purge.RunBatchProgress', 'ParentBatchNo') IS NULL
    ALTER TABLE Purge.RunBatchProgress ADD ParentBatchNo INT NULL;
GO

IF COL_LENGTH('Purge.RunBatchProgress', 'SplitDepth') IS NULL
    ALTER TABLE Purge.RunBatchProgress
        ADD SplitDepth INT NOT NULL CONSTRAINT DF_RBP_SplitDepth DEFAULT(0);
GO

/* Genealogia: dalla madre alle figlie. Serve all'analisi post-mortem, non
   al motore, che legge le slice per (RunId, BatchNo). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RBP_Parent'
               AND object_id = OBJECT_ID('Purge.RunBatchProgress'))
    CREATE NONCLUSTERED INDEX IX_RBP_Parent
        ON Purge.RunBatchProgress (RunId, ParentBatchNo)
        WHERE ParentBatchNo IS NOT NULL;
GO

SELECT
    Colonna = 'Purge.RunBatchProgress.ParentBatchNo',
    Esito   = CASE WHEN COL_LENGTH('Purge.RunBatchProgress', 'ParentBatchNo') IS NULL
                   THEN 'ASSENTE' ELSE 'OK' END
UNION ALL
SELECT
    'Purge.RunBatchProgress.SplitDepth',
    CASE WHEN COL_LENGTH('Purge.RunBatchProgress', 'SplitDepth') IS NULL
         THEN 'ASSENTE' ELSE 'OK' END;
GO
