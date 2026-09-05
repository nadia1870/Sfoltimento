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
