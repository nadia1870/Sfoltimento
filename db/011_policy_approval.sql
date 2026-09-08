/* =====================================================================
   Approvazione della policy

   La modalita' di esecuzione arriva dalla riga di comando (--dry-run o
   --delete), non dalla configurazione. Questo elimina la cancellazione
   accidentale per appsettings sbagliato, ma non l'altro caso: qualcuno
   che pianifica il job DELETE senza che nessuno abbia esaminato un
   report.

   Per quello serve un'approvazione registrata su questo database, e
   riferita all'impronta della policy corrente. Se la retention cambia,
   l'impronta cambia e l'approvazione non vale piu'.

   Idempotente.
   ===================================================================== */

SET NOCOUNT ON;
GO

IF OBJECT_ID('Purge.PolicyApproval') IS NULL
BEGIN
    CREATE TABLE Purge.PolicyApproval
    (
        /* La chiave e' la policy, non il run. Cio' che si approva e' cosa
           verra' cancellato; il dry-run e' la prova che e' stato guardato. */
        PolicyHash   CHAR(64)         NOT NULL
                     CONSTRAINT PK_PolicyApproval PRIMARY KEY,
        DryRunRunId  UNIQUEIDENTIFIER NOT NULL,
        ApprovedOn   DATETIMEOFFSET   NOT NULL,
        ApprovedBy   NVARCHAR(200)    NOT NULL,
        PolicyText   NVARCHAR(1000)   NOT NULL,
        Note         NVARCHAR(1000)   NULL
    );
END
GO

/* Ogni run registra la policy sotto cui e' girato: senza, dall'id di un
   dry-run non si risalirebbe a cosa e' stato esaminato. */
IF COL_LENGTH('Purge.PurgeRun', 'PolicyHash') IS NULL
    ALTER TABLE Purge.PurgeRun ADD PolicyHash CHAR(64) NULL;
GO

SELECT Oggetto = 'Purge.PolicyApproval',
       Esito   = CASE WHEN OBJECT_ID('Purge.PolicyApproval') IS NULL
                      THEN 'ASSENTE' ELSE 'OK' END
UNION ALL
SELECT 'Purge.PurgeRun.PolicyHash',
       CASE WHEN COL_LENGTH('Purge.PurgeRun', 'PolicyHash') IS NULL
            THEN 'ASSENTE' ELSE 'OK' END;
GO
