/* =====================================================================
   INSTALLAZIONE DELLO SCHEMA Purge — script autonomo
   Database di destinazione: OSM.PaymentOrder

   Crea 8 tabelle di controllo, 1 vista e i relativi indici.
   NON tocca lo schema PaymentOrder: per gli indici sulle tabelle
   applicative vedere 002_indexes.sql, che va valutato a parte perche'
   opera su tabelle grandi e in uso.

   Idempotente: rieseguibile senza effetti.
   ===================================================================== */

SET NOCOUNT ON;
GO

IF DB_NAME() NOT LIKE '%PaymentOrder%'
BEGIN
    RAISERROR('Database corrente: %s. Verificare di essere sul database giusto.', 16, 1, @@SERVERNAME);
    SET NOEXEC ON;
END
GO

/* =====================================================================
   Schema Purge — strutture di controllo del flusso di retention
   Rif. analisi tecnica v10, §7.1. Idempotente.
   ===================================================================== */
IF SCHEMA_ID('Purge') IS NULL EXEC('CREATE SCHEMA Purge');
GO

/* --- PurgeRun: stato del run e parametri congelati (§7.2) ------------- */
IF OBJECT_ID('Purge.PurgeRun') IS NULL
BEGIN
    CREATE TABLE Purge.PurgeRun
    (
        RunId             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_PurgeRun PRIMARY KEY,
        Strategy          VARCHAR(30)      NOT NULL,
        Phase             VARCHAR(20)      NOT NULL,
        DryRun            BIT              NOT NULL,
        AnchorMode        VARCHAR(20)      NOT NULL,
        RetentionCutoff   DATETIME2        NOT NULL,
        AbandonedCutoff   DATETIME2        NULL,
        MaxRowsPerBatch   INT              NOT NULL,
        MaxOrdersPerBatch INT              NOT NULL,
        StartedOn         DATETIMEOFFSET   NOT NULL,
        CompletedOn       DATETIMEOFFSET   NULL,
        LastError         NVARCHAR(2000)   NULL
    );
    CREATE NONCLUSTERED INDEX IX_PurgeRun_Phase ON Purge.PurgeRun (Phase, StartedOn);
END
GO

/* --- Candidati: set congelato, con peso e slice (§6.3) ---------------- */
IF OBJECT_ID('Purge.RunCandidateOrder') IS NULL
BEGIN
    CREATE TABLE Purge.RunCandidateOrder
    (
        RunId          UNIQUEIDENTIFIER NOT NULL,
        OrderId        UNIQUEIDENTIFIER NOT NULL,
        CollectiveOrderId UNIQUEIDENTIFIER NULL,
        RowWeight      INT              NULL,
        BatchNo        INT              NULL,
        IsOversized    BIT              NOT NULL CONSTRAINT DF_RCO_Over DEFAULT(0),
        State          VARCHAR(20)      NOT NULL,
        ExcludedReason VARCHAR(60)      NULL,
        CONSTRAINT PK_RunCandidateOrder PRIMARY KEY CLUSTERED (RunId, OrderId)
    );
    CREATE NONCLUSTERED INDEX IX_RCO_Batch
        ON Purge.RunCandidateOrder (RunId, BatchNo) INCLUDE (OrderId, RowWeight, CollectiveOrderId);
END
GO

IF OBJECT_ID('Purge.RunCandidateOrderHistory') IS NULL
BEGIN
    CREATE TABLE Purge.RunCandidateOrderHistory
    (
        RunId          UNIQUEIDENTIFIER NOT NULL,
        OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
        OrderId        UNIQUEIDENTIFIER NULL,
        BatchNo        INT              NULL,
        CONSTRAINT PK_RunCandidateOrderHistory PRIMARY KEY CLUSTERED (RunId, OrderHistoryId)
    );
    CREATE NONCLUSTERED INDEX IX_RCOH_Batch
        ON Purge.RunCandidateOrderHistory (RunId, BatchNo) INCLUDE (OrderHistoryId);
    CREATE NONCLUSTERED INDEX IX_RCOH_Order
        ON Purge.RunCandidateOrderHistory (RunId, OrderId);
END
GO

/* --- Collettivi: unita' atomica separata (§10.7) ---------------------- */
IF OBJECT_ID('Purge.RunCandidateCollective') IS NULL
BEGIN
    CREATE TABLE Purge.RunCandidateCollective
    (
        RunId             UNIQUEIDENTIFIER NOT NULL,
        CollectiveOrderId UNIQUEIDENTIFIER NOT NULL,
        BatchNo           INT              NULL,
        OrderCount        INT              NOT NULL,
        EstimatedRows     INT              NOT NULL,
        State             VARCHAR(20)      NOT NULL,
        ExcludedReason    VARCHAR(60)      NULL,
        CONSTRAINT PK_RunCandidateCollective PRIMARY KEY CLUSTERED (RunId, CollectiveOrderId)
    );
    CREATE NONCLUSTERED INDEX IX_RCC_Batch
        ON Purge.RunCandidateCollective (RunId, BatchNo) INCLUDE (CollectiveOrderId, State);
END
GO

/* --- Checkpoint per slice (§7.1) -------------------------------------- */
IF OBJECT_ID('Purge.RunBatchProgress') IS NULL
BEGIN
    CREATE TABLE Purge.RunBatchProgress
    (
        RunId             UNIQUEIDENTIFIER NOT NULL,
        BatchNo           INT              NOT NULL,
        Status            VARCHAR(20)      NOT NULL,
        IsOversized       BIT              NOT NULL CONSTRAINT DF_RBP_Over DEFAULT(0),
        OrderCount        INT              NOT NULL,
        EstimatedRowCount INT              NOT NULL,
        ActualDeletedRows INT              NOT NULL CONSTRAINT DF_RBP_Rows DEFAULT(0),
        AttemptCount      INT              NOT NULL CONSTRAINT DF_RBP_Att  DEFAULT(0),
        StartedOn         DATETIMEOFFSET   NULL,
        CompletedOn       DATETIMEOFFSET   NULL,
        LastError         NVARCHAR(2000)   NULL,
        CONSTRAINT PK_RunBatchProgress PRIMARY KEY CLUSTERED (RunId, BatchNo)
    );
    CREATE NONCLUSTERED INDEX IX_RBP_Pending
        ON Purge.RunBatchProgress (RunId, BatchNo) WHERE Status <> 'Completed';
END
GO

/* --- Validazioni, dry-run, audit -------------------------------------- */
IF OBJECT_ID('Purge.ValidationFinding') IS NULL
BEGIN
    CREATE TABLE Purge.ValidationFinding
    (
        Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ValidationFinding PRIMARY KEY,
        RunId         UNIQUEIDENTIFIER NOT NULL,
        RuleId        VARCHAR(10)      NOT NULL,
        TableName     NVARCHAR(128)    NULL,
        AffectedCount BIGINT           NOT NULL,
        DetectedOn    DATETIMEOFFSET   NOT NULL
    );
    CREATE NONCLUSTERED INDEX IX_VF_Run ON Purge.ValidationFinding (RunId);
END
GO

IF OBJECT_ID('Purge.DryRunReport') IS NULL
BEGIN
    CREATE TABLE Purge.DryRunReport
    (
        Id               BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DryRunReport PRIMARY KEY,
        RunId            UNIQUEIDENTIFIER NOT NULL,
        TableName        NVARCHAR(128)    NOT NULL,
        RowCountEstimate BIGINT           NOT NULL,
        ProducedOn       DATETIMEOFFSET   NOT NULL
    );
    CREATE NONCLUSTERED INDEX IX_DRR_Run ON Purge.DryRunReport (RunId, TableName);
END
GO

IF OBJECT_ID('Purge.PurgeAudit') IS NULL
BEGIN
    CREATE TABLE Purge.PurgeAudit
    (
        Id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurgeAudit PRIMARY KEY,
        RunId       UNIQUEIDENTIFIER NOT NULL,
        TableName   NVARCHAR(128)    NOT NULL,
        RowsDeleted BIGINT           NOT NULL,
        RecordedOn  DATETIMEOFFSET   NOT NULL
    );
    CREATE NONCLUSTERED INDEX IX_PA_Run ON Purge.PurgeAudit (RunId);
END
GO

IF OBJECT_ID('Purge.vDryRunVsActual') IS NOT NULL DROP VIEW Purge.vDryRunVsActual;
GO
CREATE VIEW Purge.vDryRunVsActual AS
SELECT d.RunId, d.TableName,
       Previsto    = d.RowCountEstimate,
       Effettivo   = ISNULL(a.RowsDeleted, 0),
       Scostamento = ISNULL(a.RowsDeleted, 0) - d.RowCountEstimate
FROM Purge.DryRunReport AS d
LEFT JOIN Purge.PurgeAudit AS a ON a.RunId = d.RunId AND a.TableName = d.TableName;
GO

/* =====================================================================
   PERMESSI
   Concedere all'utenza del servizio. Con Integrated Security le
   credenziali sono quelle del processo: sotto Visual Studio il proprio
   utente, sotto un servizio Windows l'account del servizio.
   Sostituire <UTENZA> e decommentare.
   ===================================================================== */
/*
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::Purge TO [<UTENZA>];

-- Sola lettura su PaymentOrder: sufficiente per il DRY-RUN.
-- Finche' non serve cancellare davvero, non concedere altro: rende
-- l'assenza di cancellazioni una proprieta' strutturale e non una
-- promessa del codice.
GRANT SELECT ON SCHEMA::PaymentOrder TO [<UTENZA>];

-- Da concedere SOLO dopo l'approvazione del report di dry-run.
-- GRANT DELETE ON SCHEMA::PaymentOrder TO [<UTENZA>];
*/
GO

/* =====================================================================
   VERIFICA
   Attesi: 8 tabelle, 1 vista. Se il conteggio non torna, lo script non
   e' andato a buon fine e il motore fallira' all'avvio.
   ===================================================================== */
SELECT
    Oggetto = QUOTENAME(s.name) + '.' + QUOTENAME(o.name),
    Tipo    = o.type_desc,
    Colonne = (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = o.object_id)
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = 'Purge' AND o.type IN ('U','V')
ORDER BY o.type_desc, o.name;

SELECT
    Tabelle = SUM(CASE WHEN o.type = 'U' THEN 1 ELSE 0 END),
    Viste   = SUM(CASE WHEN o.type = 'V' THEN 1 ELSE 0 END),
    Esito   = CASE WHEN SUM(CASE WHEN o.type = 'U' THEN 1 ELSE 0 END) = 8
                    AND SUM(CASE WHEN o.type = 'V' THEN 1 ELSE 0 END) = 1
                   THEN 'OK' ELSE 'INCOMPLETO' END
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = 'Purge' AND o.type IN ('U','V');
GO

SET NOEXEC OFF;
GO
