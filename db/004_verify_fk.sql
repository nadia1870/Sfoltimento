/* =====================================================================
   Verifica delle FK REALI del database contro PurgeTopology.
   DA ESEGUIRE PRIMA DEL MERGE.

   Tutto il disegno poggia sullo snapshot EF, che descrive il modello come
   l'applicazione crede che sia. Se il database reale diverge — vincoli
   assenti, delete rule diversa, tabelle aggiunte a mano — l'ordine
   topologico dei quattro gruppi va rifatto.
   ===================================================================== */

/* 1. Tutte le FK entranti sulle tabelle dell'aggregato Order, con la
      delete rule effettiva. Attese: NO_ACTION (Restrict) ovunque,
      tranne Model.OrderId che deve risultare CASCADE.                    */
SELECT
    TabellaFiglia  = QUOTENAME(sc.name) + '.' + QUOTENAME(tc.name),
    ColonnaFiglia  = cc.name,
    TabellaPadre   = QUOTENAME(sp.name) + '.' + QUOTENAME(tp.name),
    ColonnaPadre   = cp.name,
    DeleteRule     = fk.delete_referential_action_desc,
    Vincolo        = fk.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables  tc ON tc.object_id = fk.parent_object_id
JOIN sys.schemas sc ON sc.schema_id = tc.schema_id
JOIN sys.columns cc ON cc.object_id = tc.object_id AND cc.column_id = fkc.parent_column_id
JOIN sys.tables  tp ON tp.object_id = fk.referenced_object_id
JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.referenced_column_id
WHERE sp.name IN ('PaymentOrder')
ORDER BY sp.name, tp.name, sc.name, tc.name;

/* 2. Delete rule diverse da NO_ACTION: ogni riga qui e' una cascata che
      agirebbe senza passare dal motore, quindi senza essere conteggiata.  */
SELECT
    Vincolo     = fk.name,
    Figlia      = QUOTENAME(sc.name) + '.' + QUOTENAME(tc.name),
    Padre       = QUOTENAME(sp.name) + '.' + QUOTENAME(tp.name),
    DeleteRule  = fk.delete_referential_action_desc
FROM sys.foreign_keys fk
JOIN sys.tables  tc ON tc.object_id = fk.parent_object_id
JOIN sys.schemas sc ON sc.schema_id = tc.schema_id
JOIN sys.tables  tp ON tp.object_id = fk.referenced_object_id
JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
WHERE fk.delete_referential_action <> 0
  AND sp.name = 'PaymentOrder'
ORDER BY fk.name;

/* 3. Vincoli non fidati (untrusted): creati o riabilitati con NOCHECK.
      Il motore si appoggia alle FK come rete di sicurezza finale: un
      vincolo untrusted non garantisce nulla sui dati gia' presenti.       */
SELECT
    Vincolo   = fk.name,
    Tabella   = QUOTENAME(sc.name) + '.' + QUOTENAME(tc.name),
    Disabled  = fk.is_disabled,
    NotTrusted= fk.is_not_trusted
FROM sys.foreign_keys fk
JOIN sys.tables  tc ON tc.object_id = fk.parent_object_id
JOIN sys.schemas sc ON sc.schema_id = tc.schema_id
WHERE (fk.is_disabled = 1 OR fk.is_not_trusted = 1)
  AND sc.name IN ('PaymentOrder','Accounting','InstantPayment');

/* 4. Tabelle figlie di Order NON previste da PurgeTopology.
      Se questa query restituisce righe, il gruppo 3 e' incompleto e la
      DELETE su Order fallira' con errore 547.                            */
SELECT DISTINCT
    TabellaFiglia = QUOTENAME(sc.name) + '.' + QUOTENAME(tc.name)
FROM sys.foreign_keys fk
JOIN sys.tables  tc ON tc.object_id = fk.parent_object_id
JOIN sys.schemas sc ON sc.schema_id = tc.schema_id
JOIN sys.tables  tp ON tp.object_id = fk.referenced_object_id
WHERE tp.name = 'Order'
  AND tc.name NOT IN (
      'AccountTransfer','BankTransfer','BankTransferToCornerCard',
      'ForeignBankTransfer','InpaymentSlip','IpBankTransfer','IpQRBill',
      'QRBill','RealTimeCardReload','StandingOrder',
      'CollectiveOrderGroupOrder','Model','OrderHistory');

/* 5. Tabelle figlie di OrderHistory NON previste dal gruppo 1.           */
SELECT DISTINCT
    TabellaFiglia = QUOTENAME(sc.name) + '.' + QUOTENAME(tc.name)
FROM sys.foreign_keys fk
JOIN sys.tables  tc ON tc.object_id = fk.parent_object_id
JOIN sys.schemas sc ON sc.schema_id = tc.schema_id
JOIN sys.tables  tp ON tp.object_id = fk.referenced_object_id
WHERE tp.name = 'OrderHistory'
  AND tc.name NOT IN (
      'AccountTransferHistory','BankTransferHistory',
      'BankTransferToCornerCardHistory','ForeignBankTransferHistory',
      'InpaymentSlipHistory','IpBankTransferHistory','IpQRBillHistory',
      'QRBillHistory','RealTimeCardReloadHistory','StandingOrderHistory',
      'CollectiveOrderGroupOrderHistory');
