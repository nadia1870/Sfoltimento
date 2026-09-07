/* =====================================================================
   Ciclo di vita del run

   Due modifiche:
     - Phase allargata a VARCHAR(30). 'CompletedWithErrors' e' di 19
       caratteri e ci starebbe anche in VARCHAR(20), ma di misura: la
       prossima fase non ci starebbe, e il troncamento silenzioso di una
       colonna di stato e' un guasto che si scopre tardi.
     - Contatore delle interruzioni. Un guasto non chiude piu' il run e non
       ne cambia la fase: senza un contatore, un problema stabile lo farebbe
       ripartire ogni notte all'infinito.

   Idempotente.
   ===================================================================== */

SET NOCOUNT ON;
GO

/* L'indice va tolto e rimesso: la colonna e' una sua chiave. */
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID('Purge.PurgeRun') AND name = 'IX_PurgeRun_Phase')
    DROP INDEX IX_PurgeRun_Phase ON Purge.PurgeRun;
GO

IF COL_LENGTH('Purge.PurgeRun', 'Phase') < 30
    ALTER TABLE Purge.PurgeRun ALTER COLUMN Phase VARCHAR(30) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('Purge.PurgeRun') AND name = 'IX_PurgeRun_Phase')
    CREATE NONCLUSTERED INDEX IX_PurgeRun_Phase ON Purge.PurgeRun (Phase, StartedOn);
GO

IF COL_LENGTH('Purge.PurgeRun', 'InterruptionCount') IS NULL
    ALTER TABLE Purge.PurgeRun
        ADD InterruptionCount INT NOT NULL CONSTRAINT DF_PurgeRun_Interruptions DEFAULT(0);
GO

IF COL_LENGTH('Purge.PurgeRun', 'LastInterruptedOn') IS NULL
    ALTER TABLE Purge.PurgeRun ADD LastInterruptedOn DATETIMEOFFSET NULL;
GO

/* ---------------------------------------------------------------------
   Verifica
   --------------------------------------------------------------------- */
SELECT
    Colonna = 'Purge.PurgeRun.' + c.Nome,
    Esito   = CASE WHEN COL_LENGTH('Purge.PurgeRun', c.Nome) IS NULL
                   THEN 'ASSENTE' ELSE 'OK' END
FROM (VALUES ('InterruptionCount'), ('LastInterruptedOn')) AS c(Nome);

SELECT
    Colonna = 'Purge.PurgeRun.Phase',
    Lunghezza = COL_LENGTH('Purge.PurgeRun', 'Phase'),
    Esito = CASE WHEN COL_LENGTH('Purge.PurgeRun', 'Phase') >= 30 THEN 'OK' ELSE 'STRETTA' END;
GO
