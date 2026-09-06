/* =====================================================================
   Traccia di audit per slice

   Purge.PurgeAudit esisteva ma non veniva mai scritta: la traccia di cosa
   fosse stato cancellato viveva solo nei log e in RunBatchProgress
   .ActualDeletedRows, aggregato per slice e senza dettaglio per tabella.

   Due modifiche:
     - BatchNo sulla riga di audit, per risalire dalla riga alla slice che
       l'ha prodotta;
     - vDryRunVsActual aggrega, perche' l'audit ora e' append-only e ha una
       riga per (RunId, BatchNo, tabella): senza GROUP BY il LEFT JOIN
       moltiplicherebbe le righe del report di previsione.

   Idempotente.
   ===================================================================== */

SET NOCOUNT ON;
GO

IF COL_LENGTH('Purge.PurgeAudit', 'BatchNo') IS NULL
    ALTER TABLE Purge.PurgeAudit ADD BatchNo INT NULL;
GO

/* L'aggregazione della view e' per (RunId, TableName): senza indice
   diventa una scansione dell'intera storia di audit a ogni interrogazione. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('Purge.PurgeAudit')
                 AND name = 'IX_PA_Run_Table')
BEGIN
    CREATE NONCLUSTERED INDEX IX_PA_Run_Table
        ON Purge.PurgeAudit (RunId, TableName) INCLUDE (RowsDeleted, BatchNo);
END
GO

IF OBJECT_ID('Purge.vDryRunVsActual') IS NOT NULL DROP VIEW Purge.vDryRunVsActual;
GO

/* Previsto ed effettivo si confrontano a parita' di RunId. Perche' la view
   dica qualcosa serve che il run reale abbia prodotto anche il proprio
   baseline: e' cio' che fa PlanningPhase quando Purge:AuditBaselineEnabled
   e' attivo. Con il baseline disattivato la view resta popolata solo per i
   run di dry-run, dove Effettivo e' zero per costruzione.                */
CREATE VIEW Purge.vDryRunVsActual AS
SELECT d.RunId,
       d.TableName,
       Previsto    = d.RowCountEstimate,
       Effettivo   = ISNULL(a.RowsDeleted, 0),
       Scostamento = ISNULL(a.RowsDeleted, 0) - d.RowCountEstimate
FROM Purge.DryRunReport AS d
LEFT JOIN (
    SELECT RunId, TableName, RowsDeleted = SUM(RowsDeleted)
    FROM Purge.PurgeAudit
    GROUP BY RunId, TableName
) AS a ON a.RunId = d.RunId AND a.TableName = d.TableName;
GO

/* ---------------------------------------------------------------------
   Verifica: la colonna deve esistere, altrimenti il motore fallisce
   all'avvio in SchemaVerifier invece che alle due di notte.
   --------------------------------------------------------------------- */
SELECT
    Colonna = 'Purge.PurgeAudit.BatchNo',
    Esito   = CASE WHEN COL_LENGTH('Purge.PurgeAudit', 'BatchNo') IS NULL
                   THEN 'ASSENTE' ELSE 'OK' END;
GO
