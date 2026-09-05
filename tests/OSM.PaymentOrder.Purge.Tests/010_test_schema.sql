/* =====================================================================
   Schema PaymentOrder per i test di integrazione.

   Derivato dallo snapshot EF: contiene SOLO le colonne che il motore
   interroga, ma la TOPOLOGIA DELLE FOREIGN KEY e' identica a quella reale.
   E' la topologia l'oggetto sotto test, non i tipi delle colonne.

   Verificare periodicamente con db/004_verify_fk.sql che il database reale
   non abbia divergenze: se le acquisisce, questo file va aggiornato.
   ===================================================================== */

IF SCHEMA_ID('PaymentOrder') IS NULL EXEC('CREATE SCHEMA PaymentOrder');
GO

CREATE TABLE PaymentOrder.[Order] (
    Id                 UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
    Code               NVARCHAR(50)     NOT NULL CONSTRAINT UQ_Order_Code UNIQUE,
    StatusCode         NVARCHAR(50)     NOT NULL,
    TypeCode           NVARCHAR(100)    NOT NULL CONSTRAINT DF_Order_Type DEFAULT('BankTransfer'),
    StandingOrder      BIT              NOT NULL CONSTRAINT DF_Order_SO   DEFAULT(0),
    CollectiveOrder    BIT              NOT NULL CONSTRAINT DF_Order_CO   DEFAULT(0),
    CreationDate       DATETIME2        NOT NULL,
    ExecutionDate      DATETIME2        NOT NULL,   -- NOT NULL come in produzione
    DebtorAccountRelNr INT              NOT NULL CONSTRAINT DF_Order_Rel  DEFAULT(0),
    CreatedOn          DATETIME2        NOT NULL CONSTRAINT DF_Order_CrOn DEFAULT(SYSDATETIME()),
    UpdatedOn          DATETIME2        NOT NULL CONSTRAINT DF_Order_UpOn DEFAULT(SYSDATETIME())
);

CREATE TABLE PaymentOrder.OrderHistory (
    Id                 UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OrderHistory PRIMARY KEY,
    OrderRefId         UNIQUEIDENTIFIER NULL,      -- nullable: storici orfani (C1)
    Code               NVARCHAR(50)     NOT NULL,
    StatusCode         NVARCHAR(50)     NOT NULL,
    DebtorAccountRelNr INT              NOT NULL CONSTRAINT DF_OH_Rel DEFAULT(0),
    UpdatedOn          DATETIME2        NOT NULL CONSTRAINT DF_OH_UpOn DEFAULT(SYSDATETIME()),
    CONSTRAINT FK_OrderHistory_Order_OrderRefId
        FOREIGN KEY (OrderRefId) REFERENCES PaymentOrder.[Order](Id)
);

CREATE TABLE PaymentOrder.Category (
    Id                 UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
    Name               NVARCHAR(255)    NOT NULL,
    RelationshipNumber INT              NOT NULL CONSTRAINT DF_Cat_Rel DEFAULT(0)
);

/* Model.OrderId e' l'UNICA cascata dello schema (caso C5): senza l'esclusione
   applicativa dei candidati referenziati da Model, cancellare un ordine
   distruggerebbe silenziosamente il template dell'utente. */
CREATE TABLE PaymentOrder.Model (
    Id                 UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Model PRIMARY KEY,
    OrderId            UNIQUEIDENTIFIER NOT NULL,
    CategoryId         UNIQUEIDENTIFIER NULL,
    Name               NVARCHAR(255)    NOT NULL,
    RelationshipNumber INT              NOT NULL CONSTRAINT DF_Model_Rel DEFAULT(0),
    CONSTRAINT FK_Model_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id) ON DELETE CASCADE,
    CONSTRAINT FK_Model_Category_CategoryId
        FOREIGN KEY (CategoryId) REFERENCES PaymentOrder.Category(Id)
);
GO
CREATE TABLE PaymentOrder.AccountTransfer (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AccountTransfer PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_AccountTransfer_OrderId UNIQUE,
    CONSTRAINT FK_AccountTransfer_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.BankTransfer (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BankTransfer PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_BankTransfer_OrderId UNIQUE,
    CONSTRAINT FK_BankTransfer_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.BankTransferToCornerCard (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BankTransferToCornerCard PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_BankTransferToCornerCard_OrderId UNIQUE,
    CONSTRAINT FK_BankTransferToCornerCard_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.ForeignBankTransfer (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ForeignBankTransfer PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_ForeignBankTransfer_OrderId UNIQUE,
    CONSTRAINT FK_ForeignBankTransfer_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.InpaymentSlip (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InpaymentSlip PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_InpaymentSlip_OrderId UNIQUE,
    CONSTRAINT FK_InpaymentSlip_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.IpBankTransfer (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IpBankTransfer PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_IpBankTransfer_OrderId UNIQUE,
    CONSTRAINT FK_IpBankTransfer_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.IpQRBill (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IpQRBill PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_IpQRBill_OrderId UNIQUE,
    CONSTRAINT FK_IpQRBill_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.QRBill (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_QRBill PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_QRBill_OrderId UNIQUE,
    CONSTRAINT FK_QRBill_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.RealTimeCardReload (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RealTimeCardReload PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_RealTimeCardReload_OrderId UNIQUE,
    CONSTRAINT FK_RealTimeCardReload_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE TABLE PaymentOrder.StandingOrder (
    Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_StandingOrder PRIMARY KEY,
    OrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_StandingOrder_OrderId UNIQUE,
    CONSTRAINT FK_StandingOrder_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
/* StandingOrder porta le colonne del piano: LastExecutionDate NULL significa
   piano senza scadenza, quindi non eleggibile per quanto vecchia sia la testata. */
ALTER TABLE PaymentOrder.StandingOrder ADD
    FirstExecutionDate DATETIME2 NOT NULL CONSTRAINT DF_SO_First DEFAULT(SYSDATETIME()),
    LastExecutionDate  DATETIME2 NULL;
GO

CREATE TABLE PaymentOrder.CollectiveOrder (
    Id               UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CollectiveOrder PRIMARY KEY,
    StatusCode       NVARCHAR(50)     NOT NULL,
    ExecutionDate    DATETIME2        NULL,   -- nullable, a differenza di Order (C8)
    TotalAmount      DECIMAL(18,2)    NOT NULL CONSTRAINT DF_CO_Tot DEFAULT(0),
    TransactionCount INT              NOT NULL CONSTRAINT DF_CO_Cnt DEFAULT(0)
);

CREATE TABLE PaymentOrder.CollectiveOrderContent (
    Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_COContent PRIMARY KEY,
    CollectiveOrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_COContent UNIQUE,
    [File]            VARBINARY(MAX)   NULL,
    CONSTRAINT FK_CollectiveOrderContent_CollectiveOrder_CollectiveOrderId
        FOREIGN KEY (CollectiveOrderId) REFERENCES PaymentOrder.CollectiveOrder(Id)
);

CREATE TABLE PaymentOrder.CollectiveOrderHistory (
    Id                   UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_COHistory PRIMARY KEY,
    CollectiveOrderRefId UNIQUEIDENTIFIER NOT NULL,
    StatusCode           NVARCHAR(50)     NOT NULL,
    CONSTRAINT FK_CollectiveOrderHistory_CollectiveOrder_CollectiveOrderRefId
        FOREIGN KEY (CollectiveOrderRefId) REFERENCES PaymentOrder.CollectiveOrder(Id)
);

CREATE TABLE PaymentOrder.CollectiveOrderGroup (
    Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_COGroup PRIMARY KEY,
    CollectiveOrderId UNIQUEIDENTIFIER NOT NULL,
    Code              NVARCHAR(20)     NOT NULL,
    CONSTRAINT FK_CollectiveOrderGroup_CollectiveOrder_CollectiveOrderId
        FOREIGN KEY (CollectiveOrderId) REFERENCES PaymentOrder.CollectiveOrder(Id)
);

CREATE TABLE PaymentOrder.CollectiveOrderGroupHistory (
    Id                        UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_COGHistory PRIMARY KEY,
    CollectiveOrderGroupRefId UNIQUEIDENTIFIER NOT NULL,
    Code                      NVARCHAR(20)     NOT NULL,
    CONSTRAINT FK_CollectiveOrderGroupHistory_CollectiveOrderGroup_CollectiveOrderGroupRefId
        FOREIGN KEY (CollectiveOrderGroupRefId) REFERENCES PaymentOrder.CollectiveOrderGroup(Id)
);

/* OrderId nullable: righe respinte in validazione (C3/C12). Non diventano mai
   Order, quindi non entrano nello staging e il gruppo 3 non le tocca: e' il
   caso che faceva fallire la coda del collettivo con violazione di FK. */
CREATE TABLE PaymentOrder.CollectiveOrderGroupOrder (
    Id                      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_COGOrder PRIMARY KEY,
    CollectiveOrderGroupId  UNIQUEIDENTIFIER NOT NULL,
    OrderId                 UNIQUEIDENTIFIER NULL,
    OrderType               NVARCHAR(50)     NULL,
    ValidationErrors        NVARCHAR(2000)   NULL,
    CONSTRAINT FK_CollectiveOrderGroupOrder_CollectiveOrderGroup_CollectiveOrderGroupId
        FOREIGN KEY (CollectiveOrderGroupId) REFERENCES PaymentOrder.CollectiveOrderGroup(Id),
    CONSTRAINT FK_CollectiveOrderGroupOrder_Order_OrderId
        FOREIGN KEY (OrderId) REFERENCES PaymentOrder.[Order](Id)
);
CREATE UNIQUE INDEX UQ_COGOrder_OrderId ON PaymentOrder.CollectiveOrderGroupOrder(OrderId)
    WHERE OrderId IS NOT NULL;
GO
/* Gruppo 1 — il doppio vincolo: ogni storico referenzia SIA OrderHistory
   SIA il dettaglio corrente, entrambi con NO ACTION. Sono le foglie del
   grafo e bloccano due rami contemporaneamente. */
CREATE TABLE PaymentOrder.AccountTransferHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AccountTransferHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    AccountTransferRefId           UNIQUEIDENTIFIER NULL    ,
    CONSTRAINT FK_AccountTransferHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_AccountTransferHistory_AccountTransfer_AccountTransferRefId
        FOREIGN KEY (AccountTransferRefId) REFERENCES PaymentOrder.AccountTransfer(Id)
);
CREATE TABLE PaymentOrder.BankTransferHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BankTransferHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    BankTransferRefId              UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_BankTransferHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_BankTransferHistory_BankTransfer_BankTransferRefId
        FOREIGN KEY (BankTransferRefId) REFERENCES PaymentOrder.BankTransfer(Id)
);
CREATE TABLE PaymentOrder.BankTransferToCornerCardHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BankTransferToCornerCardHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    BankTransferToCornerCardRefId  UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_BankTransferToCornerCardHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_BankTransferToCornerCardHistory_BankTransferToCornerCard_BankTransferToCornerCardRefId
        FOREIGN KEY (BankTransferToCornerCardRefId) REFERENCES PaymentOrder.BankTransferToCornerCard(Id)
);
CREATE TABLE PaymentOrder.ForeignBankTransferHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ForeignBankTransferHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    ForeignBankTransferRefId       UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_ForeignBankTransferHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_ForeignBankTransferHistory_ForeignBankTransfer_ForeignBankTransferRefId
        FOREIGN KEY (ForeignBankTransferRefId) REFERENCES PaymentOrder.ForeignBankTransfer(Id)
);
CREATE TABLE PaymentOrder.InpaymentSlipHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InpaymentSlipHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    InpaymentSlipRefId             UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_InpaymentSlipHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_InpaymentSlipHistory_InpaymentSlip_InpaymentSlipRefId
        FOREIGN KEY (InpaymentSlipRefId) REFERENCES PaymentOrder.InpaymentSlip(Id)
);
CREATE TABLE PaymentOrder.IpBankTransferHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IpBankTransferHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    IpBankTransferRefId            UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_IpBankTransferHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_IpBankTransferHistory_IpBankTransfer_IpBankTransferRefId
        FOREIGN KEY (IpBankTransferRefId) REFERENCES PaymentOrder.IpBankTransfer(Id)
);
CREATE TABLE PaymentOrder.IpQRBillHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IpQRBillHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    IpQRBillRefId                  UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_IpQRBillHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_IpQRBillHistory_IpQRBill_IpQRBillRefId
        FOREIGN KEY (IpQRBillRefId) REFERENCES PaymentOrder.IpQRBill(Id)
);
CREATE TABLE PaymentOrder.QRBillHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_QRBillHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    QRBillRefId                    UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_QRBillHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_QRBillHistory_QRBill_QRBillRefId
        FOREIGN KEY (QRBillRefId) REFERENCES PaymentOrder.QRBill(Id)
);
CREATE TABLE PaymentOrder.RealTimeCardReloadHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RealTimeCardReloadHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    RealTimeCardReloadRefId        UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_RealTimeCardReloadHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_RealTimeCardReloadHistory_RealTimeCardReload_RealTimeCardReloadRefId
        FOREIGN KEY (RealTimeCardReloadRefId) REFERENCES PaymentOrder.RealTimeCardReload(Id)
);
CREATE TABLE PaymentOrder.StandingOrderHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_StandingOrderHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NOT NULL,
    StandingOrderRefId             UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_StandingOrderHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_StandingOrderHistory_StandingOrder_StandingOrderRefId
        FOREIGN KEY (StandingOrderRefId) REFERENCES PaymentOrder.StandingOrder(Id)
);
CREATE TABLE PaymentOrder.CollectiveOrderGroupOrderHistory (
    Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CollectiveOrderGroupOrderHistory PRIMARY KEY,
    OrderHistoryId UNIQUEIDENTIFIER NULL,
    CollectiveOrderGroupOrderRefId UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT FK_CollectiveOrderGroupOrderHistory_OrderHistory_OrderHistoryId
        FOREIGN KEY (OrderHistoryId) REFERENCES PaymentOrder.OrderHistory(Id),
    CONSTRAINT FK_CollectiveOrderGroupOrderHistory_CollectiveOrderGroupOrder_CollectiveOrderGroupOrderRefId
        FOREIGN KEY (CollectiveOrderGroupOrderRefId) REFERENCES PaymentOrder.CollectiveOrderGroupOrder(Id)
);
GO
