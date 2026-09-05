/* =====================================================================
   V5.1 — Collective atomic execution

   Tutti gli ordini di uno stesso Collective devono appartenere alla stessa
   slice, e il Collective deve essere cancellato nella stessa transazione.
   ===================================================================== */

SET NOCOUNT ON;
GO

IF COL_LENGTH('Purge.RunCandidateOrder', 'CollectiveOrderId') IS NULL
    ALTER TABLE Purge.RunCandidateOrder
        ADD CollectiveOrderId UNIQUEIDENTIFIER NULL;
GO

IF COL_LENGTH('Purge.RunCandidateCollective', 'BatchNo') IS NULL
    ALTER TABLE Purge.RunCandidateCollective
        ADD BatchNo INT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Purge.RunCandidateOrder')
      AND name = 'IX_RCO_Collective')
BEGIN
    CREATE NONCLUSTERED INDEX IX_RCO_Collective
        ON Purge.RunCandidateOrder (RunId, CollectiveOrderId)
        INCLUDE (OrderId, BatchNo, State, RowWeight);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Purge.RunCandidateCollective')
      AND name = 'IX_RCC_Batch')
BEGIN
    CREATE NONCLUSTERED INDEX IX_RCC_Batch
        ON Purge.RunCandidateCollective (RunId, BatchNo)
        INCLUDE (CollectiveOrderId, State);
END
GO
