/* =====================================================================
   Indici di supporto — schema PaymentOrder. Rif. v10 §14.
   Su tabelle grandi usare ONLINE = ON (Enterprise) o finestra di manutenzione.
   ===================================================================== */

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Order_Purge_Retention'
               AND object_id=OBJECT_ID('PaymentOrder.[Order]'))
CREATE NONCLUSTERED INDEX IX_Order_Purge_Retention
    ON PaymentOrder.[Order] (ExecutionDate)
    INCLUDE (StatusCode, StandingOrder, CollectiveOrder, CreationDate)
    WHERE StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted');
GO

/* Ordini abbandonati (§10.5): ancoraggio su CreationDate, stati diversi. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Order_Purge_Abandoned'
               AND object_id=OBJECT_ID('PaymentOrder.[Order]'))
CREATE NONCLUSTERED INDEX IX_Order_Purge_Abandoned
    ON PaymentOrder.[Order] (CreationDate)
    INCLUDE (StatusCode, CollectiveOrder)
    WHERE StatusCode IN ('Created','PartiallyAuthorised');
GO

/* Storici orfani (C1): non raggiungibili partendo da Order. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrderHistory_Orphan'
               AND object_id=OBJECT_ID('PaymentOrder.OrderHistory'))
CREATE NONCLUSTERED INDEX IX_OrderHistory_Orphan
    ON PaymentOrder.OrderHistory (UpdatedOn) WHERE OrderRefId IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CollectiveOrder_Purge'
               AND object_id=OBJECT_ID('PaymentOrder.CollectiveOrder'))
CREATE NONCLUSTERED INDEX IX_CollectiveOrder_Purge
    ON PaymentOrder.CollectiveOrder (ExecutionDate) INCLUDE (StatusCode);
GO

/* Piani ricorrenti (§10.6). La soglia e' su StandingOrder.LastExecutionDate,
   non su Order.ExecutionDate: senza questo indice la selezione scandisce
   l'intera StandingOrder, e la paginazione a chiave non ha una chiave su cui
   cercare. Filtrato sui piani con scadenza, perche' quelli senza non sono
   eleggibili (PA-6) e occuperebbero l'indice senza mai comparire nei
   risultati. La chiave di clustering (Id) e' accodata da SQL Server, ed e'
   proprio il secondo termine della filigrana. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_StandingOrder_Purge'
               AND object_id=OBJECT_ID('PaymentOrder.StandingOrder'))
CREATE NONCLUSTERED INDEX IX_StandingOrder_Purge
    ON PaymentOrder.StandingOrder (LastExecutionDate) INCLUDE (OrderId)
    WHERE LastExecutionDate IS NOT NULL;
GO
