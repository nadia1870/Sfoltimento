/* =====================================================================
   020 — Analisi del database PRIMA del dry-run

   Sola lettura. Produce l'ordine di grandezza di cio' che il motore
   selezionera', la distribuzione dei pesi per tarare MaxRowsPerBatch, le
   anomalie note e lo stato degli indici. E' la baseline con cui
   confrontare il report del dry-run (vedi 021).

   Gira in READ UNCOMMITTED: i conteggi possono differire di qualche unita'
   da quelli esatti, in cambio non prende lock condivisi su tabelle in uso.
   Su tabelle grandi le sezioni 3 e 4 possono durare minuti: eseguire fuori
   dall'orario di punta o su una replica in lettura.

   Parametri: allineare ai valori di appsettings. La soglia calcolata qui
   e' la stessa formula di RetentionPolicy.ComputeRetentionCutoff.
   ===================================================================== */

SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @RetentionYears  INT          = 5;
DECLARE @AnchorMode      VARCHAR(20)  = 'FiscalYearEnd';   -- oppure 'RollingDate'
DECLARE @AbandonedMonths INT          = 24;
DECLARE @MaxRowsPerBatch INT          = 3000;
DECLARE @MaxOrdersPerBatch INT        = 500;

DECLARE @Oggi   DATE = CAST(SYSDATETIME() AS DATE);
DECLARE @Cutoff DATETIME2 =
    CASE @AnchorMode
        WHEN 'RollingDate'   THEN DATEADD(YEAR, -@RetentionYears, @Oggi)
        WHEN 'FiscalYearEnd' THEN DATEFROMPARTS(YEAR(@Oggi) - @RetentionYears, 1, 1)
    END;
DECLARE @AbandonedCutoff DATETIME2 = DATEADD(MONTH, -@AbandonedMonths, @Oggi);
DECLARE @MinValidDate DATETIME2 = '1900-01-01';

IF @Cutoff IS NULL
BEGIN
    RAISERROR('AnchorMode non riconosciuto: usare RollingDate o FiscalYearEnd.', 16, 1);
    RETURN;
END

SELECT Sezione = '0. Parametri',
       RetentionYears = @RetentionYears, AnchorMode = @AnchorMode,
       Cutoff = @Cutoff, AbandonedCutoff = @AbandonedCutoff,
       MaxRowsPerBatch = @MaxRowsPerBatch, MaxOrdersPerBatch = @MaxOrdersPerBatch,
       Database_ = DB_NAME(), Server_ = @@SERVERNAME, Eseguito = SYSDATETIMEOFFSET();

/* =====================================================================
   1. DIMENSIONI — righe e spazio delle tabelle dell'aggregato
   ===================================================================== */
SELECT Sezione = '1. Dimensioni',
       Tabella = QUOTENAME(s.name) + '.' + QUOTENAME(t.name),
       Righe   = SUM(CASE WHEN i.index_id IN (0,1) THEN p.rows ELSE 0 END),
       MB_Dati = CAST(SUM(CASE WHEN i.index_id IN (0,1) THEN a.total_pages ELSE 0 END) * 8.0 / 1024 AS DECIMAL(12,1)),
       MB_Indici = CAST(SUM(CASE WHEN i.index_id > 1 THEN a.total_pages ELSE 0 END) * 8.0 / 1024 AS DECIMAL(12,1)),
       MB_Totale = CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(12,1))
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.indexes i ON i.object_id = t.object_id
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
WHERE s.name IN ('PaymentOrder', 'Purge')
GROUP BY s.name, t.name
ORDER BY MB_Totale DESC;

/* Spazio libero e modello di recupero: l'esecuzione reale scrive nel log
   ogni riga cancellata, in transazioni corte ma numerose. */
SELECT Sezione = '1. Log e recovery',
       RecoveryModel = d.recovery_model_desc,
       File_ = f.name, Tipo = f.type_desc,
       MB_Allocati = CAST(f.size * 8.0 / 1024 AS DECIMAL(12,1)),
       MB_Usati    = CAST(FILEPROPERTY(f.name, 'SpaceUsed') * 8.0 / 1024 AS DECIMAL(12,1)),
       Crescita    = CASE WHEN f.is_percent_growth = 1
                          THEN CAST(f.growth AS VARCHAR(10)) + '%'
                          ELSE CAST(f.growth * 8 / 1024 AS VARCHAR(10)) + ' MB' END
FROM sys.database_files f
CROSS JOIN sys.databases d
WHERE d.database_id = DB_ID();

/* =====================================================================
   2. ELEGGIBILITA' — quanti aggregati selezionerebbe ogni strategia
   Stesse condizioni delle query di selezione in RetentionSql.
   ===================================================================== */

/* 2a. Terminated: ordini in stato terminale, non ricorrenti, oltre soglia. */
SELECT Sezione = '2a. Terminated',
       Eleggibili            = COUNT(*),
       DiCuiConModello       = SUM(CASE WHEN EXISTS (SELECT 1 FROM PaymentOrder.Model m WHERE m.OrderId = o.Id) THEN 1 ELSE 0 END),
       DiCuiInCollettivo     = SUM(CASE WHEN EXISTS (SELECT 1 FROM PaymentOrder.CollectiveOrderGroupOrder c WHERE c.OrderId = o.Id) THEN 1 ELSE 0 END),
       Sentinelle            = (SELECT COUNT(*) FROM PaymentOrder.[Order] s
                                WHERE s.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
                                  AND s.StandingOrder = 0 AND s.ExecutionDate < @MinValidDate),
       PiuVecchio            = MIN(o.ExecutionDate),
       PiuRecente            = MAX(o.ExecutionDate)
FROM PaymentOrder.[Order] o
WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
  AND o.StandingOrder = 0
  AND o.ExecutionDate >= @MinValidDate
  AND o.ExecutionDate <  @Cutoff;

/* 2b. Terminated per anno: arretrato oggi e flusso annuo futuro.
       Le righe sotto la soglia sono l'arretrato; quelle sopra dicono quanti
       ordini diventeranno eleggibili ogni anno a regime. */
SELECT Sezione = '2b. Terminated per anno',
       Anno = YEAR(o.ExecutionDate),
       Ordini = COUNT(*),
       Revisioni = SUM(h.Cnt),
       Eleggibile = CASE WHEN YEAR(o.ExecutionDate) < YEAR(@Cutoff) OR
                             (o.ExecutionDate < @Cutoff AND @AnchorMode = 'RollingDate') THEN 'SI' ELSE 'no' END
FROM PaymentOrder.[Order] o
OUTER APPLY (SELECT Cnt = COUNT(*) FROM PaymentOrder.OrderHistory oh WHERE oh.OrderRefId = o.Id) h
WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
  AND o.StandingOrder = 0
  AND o.ExecutionDate >= @MinValidDate
GROUP BY YEAR(o.ExecutionDate),
         CASE WHEN YEAR(o.ExecutionDate) < YEAR(@Cutoff) OR
                   (o.ExecutionDate < @Cutoff AND @AnchorMode = 'RollingDate') THEN 'SI' ELSE 'no' END
ORDER BY Anno;

/* 2c. StandingOrders: piani ricorrenti con ultima esecuzione oltre soglia.
       LastExecutionDate NULL = piano senza scadenza: mai eleggibile (PA-6). */
SELECT Sezione = '2c. StandingOrders',
       Eleggibili = SUM(CASE WHEN so.LastExecutionDate IS NOT NULL AND so.LastExecutionDate < @Cutoff
                              AND o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
                              AND o.StandingOrder = 1 THEN 1 ELSE 0 END),
       SenzaScadenza_MaiEleggibili = SUM(CASE WHEN so.LastExecutionDate IS NULL THEN 1 ELSE 0 END),
       SenzaScadenza_Terminali     = SUM(CASE WHEN so.LastExecutionDate IS NULL
                              AND o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted') THEN 1 ELSE 0 END),
       TotalePiani = COUNT(*)
FROM PaymentOrder.StandingOrder so
JOIN PaymentOrder.[Order] o ON o.Id = so.OrderId;

/* 2d. Collettivi: eleggibili e bloccati, con il motivo del blocco.
       Un collettivo e' eleggibile solo se TUTTI i componenti sono terminali
       e oltre soglia. Le esclusioni D-10 sono conteggiate a parte. */
;WITH col AS (
    SELECT co.Id, co.StatusCode, co.ExecutionDate,
           Componenti = (SELECT COUNT(*) FROM PaymentOrder.CollectiveOrderGroup g
                         JOIN PaymentOrder.CollectiveOrderGroupOrder x ON x.CollectiveOrderGroupId = g.Id
                         WHERE g.CollectiveOrderId = co.Id AND x.OrderId IS NOT NULL),
           Residui    = (SELECT COUNT(*) FROM PaymentOrder.CollectiveOrderGroup g
                         JOIN PaymentOrder.CollectiveOrderGroupOrder x ON x.CollectiveOrderGroupId = g.Id
                         WHERE g.CollectiveOrderId = co.Id AND x.OrderId IS NULL),
           ComponentiNonEleggibili = (SELECT COUNT(*) FROM PaymentOrder.CollectiveOrderGroup g
                         JOIN PaymentOrder.CollectiveOrderGroupOrder x ON x.CollectiveOrderGroupId = g.Id
                         JOIN PaymentOrder.[Order] o ON o.Id = x.OrderId
                         WHERE g.CollectiveOrderId = co.Id
                           AND (o.StatusCode NOT IN ('Executed','Cancelled','Deleted','Refused','Extincted')
                                OR o.ExecutionDate < @MinValidDate OR o.ExecutionDate >= @Cutoff)),
           ComponentiConModello = (SELECT COUNT(*) FROM PaymentOrder.CollectiveOrderGroup g
                         JOIN PaymentOrder.CollectiveOrderGroupOrder x ON x.CollectiveOrderGroupId = g.Id
                         JOIN PaymentOrder.Model m ON m.OrderId = x.OrderId
                         WHERE g.CollectiveOrderId = co.Id),
           ComponentiRicorrenti = (SELECT COUNT(*) FROM PaymentOrder.CollectiveOrderGroup g
                         JOIN PaymentOrder.CollectiveOrderGroupOrder x ON x.CollectiveOrderGroupId = g.Id
                         JOIN PaymentOrder.[Order] o ON o.Id = x.OrderId
                         WHERE g.CollectiveOrderId = co.Id AND o.StandingOrder = 1)
    FROM PaymentOrder.CollectiveOrder co
)
SELECT Sezione = '2d. Collettivi',
       Classe = CASE
                  WHEN ExecutionDate IS NULL THEN 'ExecutionDate NULL (PA-7): mai eleggibile'
                  WHEN ExecutionDate >= @Cutoff THEN 'Sotto soglia: non ancora'
                  WHEN StatusCode NOT IN ('Executed','Cancelled','Refused','PartiallyExecuted') THEN 'Stato non terminale: ' + StatusCode
                  WHEN ComponentiNonEleggibili > 0 THEN 'Bloccato da componenti non eleggibili'
                  WHEN ComponentiConModello > 0 THEN 'Escluso D-10: componente con modello'
                  ELSE 'ELEGGIBILE'
                END,
       Collettivi = COUNT(*),
       Componenti = SUM(Componenti),
       RigheResidue_OrderIdNull = SUM(Residui),
       ConComponentiRicorrenti = SUM(CASE WHEN ComponentiRicorrenti > 0 THEN 1 ELSE 0 END),
       PiuVecchio = MIN(ExecutionDate), PiuRecente = MAX(ExecutionDate)
FROM col
GROUP BY CASE
                  WHEN ExecutionDate IS NULL THEN 'ExecutionDate NULL (PA-7): mai eleggibile'
                  WHEN ExecutionDate >= @Cutoff THEN 'Sotto soglia: non ancora'
                  WHEN StatusCode NOT IN ('Executed','Cancelled','Refused','PartiallyExecuted') THEN 'Stato non terminale: ' + StatusCode
                  WHEN ComponentiNonEleggibili > 0 THEN 'Bloccato da componenti non eleggibili'
                  WHEN ComponentiConModello > 0 THEN 'Escluso D-10: componente con modello'
                  ELSE 'ELEGGIBILE'
                END
ORDER BY Collettivi DESC;

/* 2e. Ordini appartenenti a piu' collettivi (AmbiguousMembership, D-10).
       Atteso zero se esiste un vincolo univoco su OrderId. */
SELECT Sezione = '2e. Appartenenze ambigue',
       OrdiniInPiuCollettivi = COUNT(*)
FROM (SELECT x.OrderId
      FROM PaymentOrder.CollectiveOrderGroupOrder x
      JOIN PaymentOrder.CollectiveOrderGroup g ON g.Id = x.CollectiveOrderGroupId
      WHERE x.OrderId IS NOT NULL
      GROUP BY x.OrderId
      HAVING COUNT(DISTINCT g.CollectiveOrderId) > 1) a;

/* 2f. Storici orfani per anno (C1). */
SELECT Sezione = '2f. Storici orfani',
       Anno = YEAR(UpdatedOn), Righe = COUNT(*),
       Eleggibile = CASE WHEN MAX(UpdatedOn) < @Cutoff THEN 'SI' ELSE 'in parte/no' END
FROM PaymentOrder.OrderHistory
WHERE OrderRefId IS NULL
GROUP BY YEAR(UpdatedOn)
ORDER BY Anno;

/* 2g. Abbandonati (AbandonedEnabled = false oggi, PA-21): solo censimento. */
SELECT Sezione = '2g. Abbandonati (non attivi)',
       StatusCode,
       OltreSoglia = SUM(CASE WHEN CreationDate < @AbandonedCutoff THEN 1 ELSE 0 END),
       Totale = COUNT(*),
       PiuVecchio = MIN(CreationDate)
FROM PaymentOrder.[Order]
WHERE StatusCode IN ('Created','PartiallyAuthorised')
GROUP BY StatusCode;

/* =====================================================================
   3. PESI E SLICE — per tarare MaxRowsPerBatch / MaxOrdersPerBatch
   Peso = 1 + 2 x revisioni (ExpandAndWeigh). Solo gli eleggibili Terminated,
   che sono la popolazione che conta.
   ===================================================================== */
;WITH elig AS (
    SELECT o.Id, Peso = 1 + 2 * ISNULL(h.Cnt, 0), Revisioni = ISNULL(h.Cnt, 0)
    FROM PaymentOrder.[Order] o
    OUTER APPLY (SELECT Cnt = COUNT(*) FROM PaymentOrder.OrderHistory oh WHERE oh.OrderRefId = o.Id) h
    WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
      AND o.StandingOrder = 0
      AND o.ExecutionDate >= @MinValidDate
      AND o.ExecutionDate <  @Cutoff
      AND NOT EXISTS (SELECT 1 FROM PaymentOrder.Model m WHERE m.OrderId = o.Id)
      AND NOT EXISTS (SELECT 1 FROM PaymentOrder.CollectiveOrderGroupOrder c WHERE c.OrderId = o.Id)
),
pct AS (
    SELECT DISTINCT
           P50 = PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY Peso) OVER (),
           P95 = PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY Peso) OVER (),
           P99 = PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY Peso) OVER ()
    FROM elig
)
SELECT Sezione = '3a. Pesi (Terminated eleggibili)',
       Ordini = (SELECT COUNT(*) FROM elig),
       RigheTotali_Stima = (SELECT SUM(Peso) FROM elig),
       PesoMedio = (SELECT AVG(Peso * 1.0) FROM elig),
       PesoP50 = pct.P50, PesoP95 = pct.P95, PesoP99 = pct.P99,
       PesoMax = (SELECT MAX(Peso) FROM elig),
       RevisioniMax = (SELECT MAX(Revisioni) FROM elig),
       Oversized_OltreMaxRows = (SELECT COUNT(*) FROM elig WHERE Peso > @MaxRowsPerBatch),
       SliceStimate = CASE WHEN (SELECT COUNT(*) FROM elig) = 0 THEN 0 ELSE
                      (SELECT MAX(v) FROM (VALUES
                          (CEILING((SELECT SUM(Peso) * 1.0 FROM elig) / @MaxRowsPerBatch)),
                          (CEILING((SELECT COUNT(*) * 1.0 FROM elig) / @MaxOrdersPerBatch))) AS x(v)) END
FROM pct;

/* 3b. Istogramma dei pesi: dove sta la massa. */
;WITH elig AS (
    SELECT Peso = 1 + 2 * ISNULL(h.Cnt, 0)
    FROM PaymentOrder.[Order] o
    OUTER APPLY (SELECT Cnt = COUNT(*) FROM PaymentOrder.OrderHistory oh WHERE oh.OrderRefId = o.Id) h
    WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
      AND o.StandingOrder = 0 AND o.ExecutionDate >= @MinValidDate AND o.ExecutionDate < @Cutoff
)
SELECT Sezione = '3b. Istogramma pesi',
       Fascia = CASE WHEN Peso <= 3 THEN '01: <=3 (0-1 revisioni)'
                     WHEN Peso <= 11 THEN '02: 4-11 (2-5 revisioni)'
                     WHEN Peso <= 41 THEN '03: 12-41 (6-20 revisioni)'
                     WHEN Peso <= 201 THEN '04: 42-201 (21-100 revisioni)'
                     WHEN Peso <= @MaxRowsPerBatch THEN '05: 202-MaxRows'
                     ELSE '06: > MaxRows (oversized)' END,
       Ordini = COUNT(*), Righe = SUM(Peso)
FROM elig
GROUP BY CASE WHEN Peso <= 3 THEN '01: <=3 (0-1 revisioni)'
              WHEN Peso <= 11 THEN '02: 4-11 (2-5 revisioni)'
              WHEN Peso <= 41 THEN '03: 12-41 (6-20 revisioni)'
              WHEN Peso <= 201 THEN '04: 42-201 (21-100 revisioni)'
              WHEN Peso <= @MaxRowsPerBatch THEN '05: 202-MaxRows'
              ELSE '06: > MaxRows (oversized)' END
ORDER BY Fascia;

/* 3c. I 20 ordini eleggibili piu' pesanti: candidati a slice dedicate. */
SELECT TOP (20) Sezione = '3c. Top oversized',
       o.Id, o.StatusCode, o.ExecutionDate, Revisioni = h.Cnt, Peso = 1 + 2 * h.Cnt
FROM PaymentOrder.[Order] o
CROSS APPLY (SELECT Cnt = COUNT(*) FROM PaymentOrder.OrderHistory oh WHERE oh.OrderRefId = o.Id) h
WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
  AND o.StandingOrder = 0 AND o.ExecutionDate >= @MinValidDate AND o.ExecutionDate < @Cutoff
ORDER BY h.Cnt DESC;

/* =====================================================================
   4. RIGHE PER TABELLA — stima di cio' che il dry-run riportera'
   Stesse join delle DELETE. Terminated soltanto: e' la strategia che
   muove i volumi. Una tabella con zero righe qui e zero nel report e'
   normale: un ordine ha un solo tipo di dettaglio.
   ===================================================================== */
;WITH elig AS (
    SELECT o.Id
    FROM PaymentOrder.[Order] o
    WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
      AND o.StandingOrder = 0 AND o.ExecutionDate >= @MinValidDate AND o.ExecutionDate < @Cutoff
      AND NOT EXISTS (SELECT 1 FROM PaymentOrder.Model m WHERE m.OrderId = o.Id)
      AND NOT EXISTS (SELECT 1 FROM PaymentOrder.CollectiveOrderGroupOrder c WHERE c.OrderId = o.Id)
),
hist AS (SELECT oh.Id FROM PaymentOrder.OrderHistory oh JOIN elig e ON e.Id = oh.OrderRefId)
SELECT Sezione = '4. Righe stimate (Terminated)', Tabella = t.Tabella, Righe = t.Righe
FROM (VALUES
    ('Order',        (SELECT COUNT_BIG(*) FROM elig)),
    ('OrderHistory', (SELECT COUNT_BIG(*) FROM hist)),
    ('AccountTransfer',          (SELECT COUNT_BIG(*) FROM PaymentOrder.AccountTransfer d JOIN elig e ON e.Id = d.OrderId)),
    ('BankTransfer',             (SELECT COUNT_BIG(*) FROM PaymentOrder.BankTransfer d JOIN elig e ON e.Id = d.OrderId)),
    ('BankTransferToCornerCard', (SELECT COUNT_BIG(*) FROM PaymentOrder.BankTransferToCornerCard d JOIN elig e ON e.Id = d.OrderId)),
    ('ForeignBankTransfer',      (SELECT COUNT_BIG(*) FROM PaymentOrder.ForeignBankTransfer d JOIN elig e ON e.Id = d.OrderId)),
    ('InpaymentSlip',            (SELECT COUNT_BIG(*) FROM PaymentOrder.InpaymentSlip d JOIN elig e ON e.Id = d.OrderId)),
    ('IpBankTransfer',           (SELECT COUNT_BIG(*) FROM PaymentOrder.IpBankTransfer d JOIN elig e ON e.Id = d.OrderId)),
    ('IpQRBill',                 (SELECT COUNT_BIG(*) FROM PaymentOrder.IpQRBill d JOIN elig e ON e.Id = d.OrderId)),
    ('QRBill',                   (SELECT COUNT_BIG(*) FROM PaymentOrder.QRBill d JOIN elig e ON e.Id = d.OrderId)),
    ('RealTimeCardReload',       (SELECT COUNT_BIG(*) FROM PaymentOrder.RealTimeCardReload d JOIN elig e ON e.Id = d.OrderId)),
    ('AccountTransferHistory',          (SELECT COUNT_BIG(*) FROM PaymentOrder.AccountTransferHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('BankTransferHistory',             (SELECT COUNT_BIG(*) FROM PaymentOrder.BankTransferHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('BankTransferToCornerCardHistory', (SELECT COUNT_BIG(*) FROM PaymentOrder.BankTransferToCornerCardHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('ForeignBankTransferHistory',      (SELECT COUNT_BIG(*) FROM PaymentOrder.ForeignBankTransferHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('InpaymentSlipHistory',            (SELECT COUNT_BIG(*) FROM PaymentOrder.InpaymentSlipHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('IpBankTransferHistory',           (SELECT COUNT_BIG(*) FROM PaymentOrder.IpBankTransferHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('IpQRBillHistory',                 (SELECT COUNT_BIG(*) FROM PaymentOrder.IpQRBillHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('QRBillHistory',                   (SELECT COUNT_BIG(*) FROM PaymentOrder.QRBillHistory h JOIN hist x ON x.Id = h.OrderHistoryId)),
    ('RealTimeCardReloadHistory',       (SELECT COUNT_BIG(*) FROM PaymentOrder.RealTimeCardReloadHistory h JOIN hist x ON x.Id = h.OrderHistoryId))
) AS t(Tabella, Righe)
ORDER BY Righe DESC;

/* =====================================================================
   5. ANOMALIE che il motore intercetterebbe in Validating (V1, V5)
   ===================================================================== */

/* 5a. C7 — storico di dettaglio che punta a un dettaglio di un altro ordine.
       Solo per la tabella piu' comune; replicare per le altre se > 0. */
SELECT Sezione = '5a. CrossRef BankTransferHistory',
       Righe = COUNT(*)
FROM PaymentOrder.BankTransferHistory h
JOIN PaymentOrder.OrderHistory oh ON oh.Id = h.OrderHistoryId
JOIN PaymentOrder.BankTransfer d ON d.Id = h.BankTransferRefId
WHERE oh.OrderRefId IS NOT NULL AND d.OrderId <> oh.OrderRefId;

/* 5b. Storici di dettaglio senza storico testata (orfani di secondo livello). */
SELECT Sezione = '5b. Dettaglio-storico senza OrderHistory',
       Tabella = t.Tabella, Righe = t.Righe
FROM (VALUES
    ('BankTransferHistory', (SELECT COUNT_BIG(*) FROM PaymentOrder.BankTransferHistory h
                             WHERE NOT EXISTS (SELECT 1 FROM PaymentOrder.OrderHistory oh WHERE oh.Id = h.OrderHistoryId))),
    ('QRBillHistory',       (SELECT COUNT_BIG(*) FROM PaymentOrder.QRBillHistory h
                             WHERE NOT EXISTS (SELECT 1 FROM PaymentOrder.OrderHistory oh WHERE oh.Id = h.OrderHistoryId))),
    ('StandingOrderHistory',(SELECT COUNT_BIG(*) FROM PaymentOrder.StandingOrderHistory h
                             WHERE NOT EXISTS (SELECT 1 FROM PaymentOrder.OrderHistory oh WHERE oh.Id = h.OrderHistoryId)))
) AS t(Tabella, Righe);

/* 5c. Modelli che puntano a ordini eleggibili: esclusi dal purge per
       costruzione, ma sono template legati a ordini di piu' di N anni. */
SELECT Sezione = '5c. Modelli su ordini oltre soglia',
       Modelli = COUNT(*), Ordini = COUNT(DISTINCT m.OrderId)
FROM PaymentOrder.Model m
JOIN PaymentOrder.[Order] o ON o.Id = m.OrderId
WHERE o.StatusCode IN ('Executed','Cancelled','Deleted','Refused','Extincted')
  AND o.ExecutionDate >= @MinValidDate AND o.ExecutionDate < @Cutoff;

/* =====================================================================
   6. INDICI di 002_indexes.sql — senza, la selezione scandisce Order
   ===================================================================== */
SELECT Sezione = '6. Indici',
       Indice = a.Nome,
       Tabella = a.Tabella,
       Presente = CASE WHEN i.name IS NULL THEN 'NO — creare prima del dry-run' ELSE 'si' END,
       Filtro = i.filter_definition,
       MB = CAST(ISNULL((SELECT SUM(au.total_pages) FROM sys.partitions p
                        JOIN sys.allocation_units au ON au.container_id = p.partition_id
                        WHERE p.object_id = i.object_id AND p.index_id = i.index_id), 0) * 8.0 / 1024 AS DECIMAL(10,1))
FROM (VALUES
    ('IX_Order_Purge_Retention', 'PaymentOrder.[Order]'),
    ('IX_Order_Purge_Abandoned', 'PaymentOrder.[Order]'),
    ('IX_OrderHistory_Orphan',   'PaymentOrder.OrderHistory'),
    ('IX_CollectiveOrder_Purge', 'PaymentOrder.CollectiveOrder'),
    ('IX_StandingOrder_Purge',   'PaymentOrder.StandingOrder')
) AS a(Nome, Tabella)
LEFT JOIN sys.indexes i ON i.name = a.Nome AND i.object_id = OBJECT_ID(a.Tabella);

/* Indici a supporto delle DELETE: ogni FK entrante nell'aggregato dovrebbe
   avere un indice sulla colonna figlia, altrimenti ogni DELETE sul padre
   scandisce la tabella figlia per verificare il vincolo. */
SELECT Sezione = '6b. FK senza indice sulla colonna figlia',
       TabellaFiglia = QUOTENAME(s.name) + '.' + QUOTENAME(t.name),
       Colonna = c.name,
       Vincolo = fk.name,
       Righe = (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1))
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables t ON t.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id AND c.column_id = fkc.parent_column_id
JOIN sys.tables tp ON tp.object_id = fk.referenced_object_id
JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
WHERE sp.name = 'PaymentOrder'
  AND NOT EXISTS (
        SELECT 1 FROM sys.index_columns ic
        WHERE ic.object_id = t.object_id AND ic.column_id = c.column_id
          AND ic.key_ordinal = 1)
ORDER BY Righe DESC;

/* =====================================================================
   7. STATO DELLO SCHEMA Purge — run precedenti, se esistono
   ===================================================================== */
IF OBJECT_ID('Purge.PurgeRun') IS NOT NULL
    SELECT Sezione = '7. Run esistenti', Strategy, Phase, DryRun, Run_ = COUNT(*),
           Ultimo = MAX(StartedOn), ConStaging = SUM(CASE WHEN StagingPurgedOn IS NULL THEN 1 ELSE 0 END)
    FROM Purge.PurgeRun
    GROUP BY Strategy, Phase, DryRun
    ORDER BY Strategy, Phase;
ELSE
    SELECT Sezione = '7. Run esistenti', Nota = 'Schema Purge non installato: eseguire 000_install_purge.sql';
