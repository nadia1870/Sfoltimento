using System.Text;

namespace OSM.PaymentOrder.Purge.Sql;

/// <summary>
/// Statement del flusso di retention. Tutti i valori variabili passano come
/// parametri ADO: nel testo compaiono solo nomi di tabella provenienti da
/// PurgeTopology, mai da input esterno.
/// </summary>
public static class RetentionSql
{
    private const string S = PurgeTopology.Schema;

    /// <summary>
    /// Guardia contro il valore sentinella (§10.2). Order.ExecutionDate è
    /// NOT NULL: se l'applicazione non la valorizza, datetime2 riceve
    /// 0001-01-01, che supera qualsiasi soglia di retention.
    /// </summary>
    public const string MinValidDate = "1900-01-01";

    public const string TerminalStates =
        "'Executed','Cancelled','Deleted','Refused','Extincted'";

    // =================================================================
    // FASE 1 — SELEZIONE
    // =================================================================

    // -----------------------------------------------------------------
    // Le selezioni procedono a pagine, non con un unico INSERT..SELECT.
    //
    // Su dati storici quello statement apriva una transazione implicita
    // sull'intera tabella Order: log in crescita e lock lunghi proprio nel
    // punto in cui tutto il resto del motore evita di prenderne.
    //
    // La paginazione e' a chiave, non con OFFSET ne' con un TOP filtrato da
    // NOT EXISTS: entrambi riscandiscono dall'inizio a ogni giro e
    // abbandonano l'indice filtrato di 002_indexes.sql, rendendo il ciclo
    // piu' lento dello statement unico che sostituisce. Ogni pagina riparte
    // invece da (@LastAnchor, @LastId), che e' esattamente la chiave di quel
    // l'indice: la colonna data piu' la chiave di clustering che SQL Server
    // vi accoda. Il costo totale resta una passata sola.
    //
    // Il NOT EXISTS sullo staging resta, ma come guardia di idempotenza e non
    // come meccanismo di avanzamento: se il processo cade a meta' selezione
    // la fase viene rieseguita da capo e le righe gia' presenti non vengono
    // duplicate. La ripresa ricomincia dalla prima pagina, perche' la
    // filigrana vive in memoria: e' una riscansione dell'indice, non una
    // riselezione, e la si paga solo dopo un'interruzione.
    // -----------------------------------------------------------------

    /// <summary>Ordini conclusi oltre la soglia (§10.4). Una pagina.</summary>
    public const string SelectTerminated = $"""
        SET NOCOUNT ON;

        DECLARE @page TABLE (OrderId UNIQUEIDENTIFIER PRIMARY KEY,
                             Anchor  DATETIME2 NOT NULL);
        DECLARE @Scanned INT, @Inserted INT;

        INSERT INTO @page (OrderId, Anchor)
        SELECT TOP (@BatchSize) o.Id, o.ExecutionDate
        FROM {S}.[Order] AS o
        WHERE o.StatusCode IN ({TerminalStates})
          AND o.StandingOrder = 0
          AND o.ExecutionDate >= '{MinValidDate}'
          AND o.ExecutionDate <  @Cutoff
          AND (o.ExecutionDate >  @LastAnchor
            OR (o.ExecutionDate = @LastAnchor AND o.Id > @LastId))
          AND NOT EXISTS (SELECT 1 FROM {S}.Model AS m WHERE m.OrderId = o.Id)
          AND NOT EXISTS (SELECT 1 FROM {S}.CollectiveOrderGroupOrder AS c
                          WHERE c.OrderId = o.Id)
        ORDER BY o.ExecutionDate, o.Id;

        SET @Scanned = @@ROWCOUNT;

        INSERT INTO Purge.RunCandidateOrder (RunId, OrderId, State)
        SELECT @RunId, p.OrderId, 'Selected'
        FROM @page AS p
        WHERE NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrder AS x
                          WHERE x.RunId = @RunId AND x.OrderId = p.OrderId);

        SET @Inserted = @@ROWCOUNT;

        SELECT Inserted   = @Inserted,
               Scanned    = @Scanned,
               NextAnchor = (SELECT MAX(Anchor) FROM @page),
               NextId     = (SELECT TOP (1) OrderId FROM @page
                             ORDER BY Anchor DESC, OrderId DESC);
        """;

    /// <summary>
    /// Ordini abbandonati (§10.5): mai autorizzati, quindi privi di scrittura
    /// contabile. Ancoraggio su CreationDate e soglia dedicata, che e' anche
    /// la chiave di IX_Order_Purge_Abandoned su cui pagina.
    /// Gli stati attivi restano esclusi: un ordine vecchio in Processing o
    /// Suspended è un'anomalia da investigare, non un candidato.
    /// </summary>
    public const string SelectAbandoned = $"""
        SET NOCOUNT ON;

        DECLARE @page TABLE (OrderId UNIQUEIDENTIFIER PRIMARY KEY,
                             Anchor  DATETIME2 NOT NULL);
        DECLARE @Scanned INT, @Inserted INT;

        INSERT INTO @page (OrderId, Anchor)
        SELECT TOP (@BatchSize) o.Id, o.CreationDate
        FROM {S}.[Order] AS o
        WHERE o.StatusCode IN ('Created','PartiallyAuthorised')
          AND o.CreationDate < @Cutoff
          AND (o.CreationDate >  @LastAnchor
            OR (o.CreationDate = @LastAnchor AND o.Id > @LastId))
          AND NOT EXISTS (SELECT 1 FROM {S}.Model AS m WHERE m.OrderId = o.Id)
          AND NOT EXISTS (SELECT 1 FROM {S}.CollectiveOrderGroupOrder AS c
                          WHERE c.OrderId = o.Id)
        ORDER BY o.CreationDate, o.Id;

        SET @Scanned = @@ROWCOUNT;

        INSERT INTO Purge.RunCandidateOrder (RunId, OrderId, State)
        SELECT @RunId, p.OrderId, 'Selected'
        FROM @page AS p
        WHERE NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrder AS x
                          WHERE x.RunId = @RunId AND x.OrderId = p.OrderId);

        SET @Inserted = @@ROWCOUNT;

        SELECT Inserted   = @Inserted,
               Scanned    = @Scanned,
               NextAnchor = (SELECT MAX(Anchor) FROM @page),
               NextId     = (SELECT TOP (1) OrderId FROM @page
                             ORDER BY Anchor DESC, OrderId DESC);
        """;

    /// <summary>
    /// Piani ricorrenti (§10.6). LastExecutionDate NULL significa piano senza
    /// scadenza: non eleggibile per quanto vecchia sia la testata. È il rischio
    /// funzionale più grave dell'intera soluzione.
    ///
    /// Qui la filigrana e' su StandingOrder, non su Order: il predicato di
    /// soglia e' su so.LastExecutionDate, quindi e' quella la colonna che
    /// guida la scansione (indice IX_StandingOrder_Purge).
    /// </summary>
    public const string SelectStandingOrders = $"""
        SET NOCOUNT ON;

        DECLARE @page TABLE (StandingOrderId UNIQUEIDENTIFIER PRIMARY KEY,
                             OrderId         UNIQUEIDENTIFIER NOT NULL,
                             Anchor          DATETIME2 NOT NULL);
        DECLARE @Scanned INT, @Inserted INT;

        INSERT INTO @page (StandingOrderId, OrderId, Anchor)
        SELECT TOP (@BatchSize) so.Id, o.Id, so.LastExecutionDate
        FROM {S}.StandingOrder AS so
        INNER JOIN {S}.[Order] AS o ON o.Id = so.OrderId
        WHERE so.LastExecutionDate IS NOT NULL
          AND so.LastExecutionDate < @Cutoff
          AND (so.LastExecutionDate >  @LastAnchor
            OR (so.LastExecutionDate = @LastAnchor AND so.Id > @LastId))
          AND o.StatusCode IN ({TerminalStates})
          AND o.StandingOrder = 1
          AND NOT EXISTS (SELECT 1 FROM {S}.Model AS m WHERE m.OrderId = o.Id)
          AND NOT EXISTS (SELECT 1 FROM {S}.CollectiveOrderGroupOrder AS c
                          WHERE c.OrderId = o.Id)
        ORDER BY so.LastExecutionDate, so.Id;

        SET @Scanned = @@ROWCOUNT;

        INSERT INTO Purge.RunCandidateOrder (RunId, OrderId, State)
        SELECT @RunId, p.OrderId, 'Selected'
        FROM @page AS p
        WHERE NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrder AS x
                          WHERE x.RunId = @RunId AND x.OrderId = p.OrderId);

        SET @Inserted = @@ROWCOUNT;

        SELECT Inserted   = @Inserted,
               Scanned    = @Scanned,
               NextAnchor = (SELECT MAX(Anchor) FROM @page),
               NextId     = (SELECT TOP (1) StandingOrderId FROM @page
                             ORDER BY Anchor DESC, StandingOrderId DESC);
        """;

    /// <summary>
    /// Collettivi integralmente eleggibili (§10.7).
    /// CollectiveOrder.ExecutionDate è NULLABLE, a differenza di Order: il
    /// predicato « data &lt; soglia » è falso quando la data manca, quindi
    /// quei collettivi non diventerebbero mai eleggibili. Sono esclusi
    /// esplicitamente e censiti come anomalia (PA-7, opzione prudente).
    /// </summary>
    public const string SelectEligibleCollectives = $"""
        INSERT INTO Purge.RunCandidateCollective
            (RunId, CollectiveOrderId, OrderCount, EstimatedRows, State)
        SELECT @RunId, co.Id,
               OrderCount    = COUNT(cgo.Id),
               EstimatedRows = COUNT(cgo.Id) * 4 + 5,
               'Selected'
        FROM {S}.CollectiveOrder AS co
        LEFT JOIN {S}.CollectiveOrderGroup AS cg      ON cg.CollectiveOrderId = co.Id
        LEFT JOIN {S}.CollectiveOrderGroupOrder AS cgo ON cgo.CollectiveOrderGroupId = cg.Id
        WHERE co.ExecutionDate IS NOT NULL
          AND co.ExecutionDate < @Cutoff
          AND NOT EXISTS (SELECT 1 FROM Purge.RunCandidateCollective AS existing
                          WHERE existing.RunId = @RunId AND existing.CollectiveOrderId = co.Id)
          AND co.StatusCode IN ('Executed','Cancelled','Refused','PartiallyExecuted')
          AND NOT EXISTS (
                SELECT 1
                FROM {S}.CollectiveOrderGroup AS g
                INNER JOIN {S}.CollectiveOrderGroupOrder AS x
                        ON x.CollectiveOrderGroupId = g.Id
                INNER JOIN {S}.[Order] AS o ON o.Id = x.OrderId
                WHERE g.CollectiveOrderId = co.Id
                  AND (o.StatusCode NOT IN ({TerminalStates})
                       OR o.ExecutionDate <  '{MinValidDate}'
                       OR o.ExecutionDate >= @Cutoff))
        GROUP BY co.Id;
        """;

    /// <summary>Collettivi senza data: censiti come anomalia, mai cancellati.</summary>
    public const string SelectCollectivesWithoutDate = $"""
        INSERT INTO Purge.RunCandidateCollective
            (RunId, CollectiveOrderId, OrderCount, EstimatedRows, State, ExcludedReason)
        SELECT @RunId, co.Id, 0, 0, 'Excluded', 'ExecutionDateNull'
        FROM {S}.CollectiveOrder AS co
        WHERE co.ExecutionDate IS NULL
          AND NOT EXISTS (SELECT 1 FROM Purge.RunCandidateCollective AS x
                          WHERE x.RunId = @RunId AND x.CollectiveOrderId = co.Id);
        """;

    /// <summary>Un Order deve appartenere a un solo Collective per il run atomico.</summary>
    public const string ValidateOrderBelongsToSingleCollective = $"""
        SELECT COUNT(*)
        FROM (
            SELECT cgo.OrderId
            FROM Purge.RunCandidateCollective AS rc
            INNER JOIN {S}.CollectiveOrderGroup AS cg ON cg.CollectiveOrderId = rc.CollectiveOrderId
            INNER JOIN {S}.CollectiveOrderGroupOrder AS cgo ON cgo.CollectiveOrderGroupId = cg.Id
            WHERE rc.RunId = @RunId AND rc.State = 'Selected' AND cgo.OrderId IS NOT NULL
            GROUP BY cgo.OrderId
            HAVING COUNT(DISTINCT rc.CollectiveOrderId) > 1
        ) AS invalid;
        """;

    /// <summary>Ordini appartenenti ai collettivi eleggibili.</summary>
    public const string SelectCollectiveComponents = $"""
        INSERT INTO Purge.RunCandidateOrder (RunId, OrderId, CollectiveOrderId, State)
        SELECT DISTINCT @RunId, cgo.OrderId, rc.CollectiveOrderId, 'Selected'
        FROM Purge.RunCandidateCollective AS rc
        INNER JOIN {S}.CollectiveOrderGroup AS cg ON cg.CollectiveOrderId = rc.CollectiveOrderId
        INNER JOIN {S}.CollectiveOrderGroupOrder AS cgo ON cgo.CollectiveOrderGroupId = cg.Id
        WHERE rc.RunId = @RunId AND rc.State = 'Selected' AND cgo.OrderId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrder AS existing
                          WHERE existing.RunId = @RunId AND existing.OrderId = cgo.OrderId);
        """;

    /// <summary>
    /// Storici orfani (C1): non raggiungibili partendo da Order.
    /// Pagina su UpdatedOn, chiave di IX_OrderHistory_Orphan.
    /// </summary>
    public const string SelectOrphanHistory = $"""
        SET NOCOUNT ON;

        DECLARE @page TABLE (OrderHistoryId UNIQUEIDENTIFIER PRIMARY KEY,
                             Anchor         DATETIME2 NOT NULL);
        DECLARE @Scanned INT, @Inserted INT;

        INSERT INTO @page (OrderHistoryId, Anchor)
        SELECT TOP (@BatchSize) oh.Id, oh.UpdatedOn
        FROM {S}.OrderHistory AS oh
        WHERE oh.OrderRefId IS NULL
          AND oh.UpdatedOn < @Cutoff
          AND (oh.UpdatedOn >  @LastAnchor
            OR (oh.UpdatedOn = @LastAnchor AND oh.Id > @LastId))
        ORDER BY oh.UpdatedOn, oh.Id;

        SET @Scanned = @@ROWCOUNT;

        INSERT INTO Purge.RunCandidateOrderHistory (RunId, OrderHistoryId, OrderId)
        SELECT @RunId, p.OrderHistoryId, NULL
        FROM @page AS p
        WHERE NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrderHistory AS x
                          WHERE x.RunId = @RunId AND x.OrderHistoryId = p.OrderHistoryId);

        SET @Inserted = @@ROWCOUNT;

        SELECT Inserted   = @Inserted,
               Scanned    = @Scanned,
               NextAnchor = (SELECT MAX(Anchor) FROM @page),
               NextId     = (SELECT TOP (1) OrderHistoryId FROM @page
                             ORDER BY Anchor DESC, OrderHistoryId DESC);
        """;

    // =================================================================
    // FASE 2 — ESPANSIONE
    // =================================================================

    /// <summary>
    /// Espansione degli storici e calcolo del peso, una pagina di candidati
    /// per volta.
    ///
    /// Le due operazioni erano due passate distinte sull'intero set. Il peso
    /// di un ordine dipende pero' solo dai propri storici, quindi si calcola
    /// sulla stessa pagina appena espansa: una passata invece di due, e nessun
    /// momento in cui l'intero set di candidati e' sotto UPDATE.
    ///
    /// La filigrana e' il solo OrderId, che e' la chiave clusterizzata dello
    /// staging dopo RunId: la pagina e' una scansione di intervallo.
    /// Un OrderId tutto a zero non verrebbe mai selezionato, perche' la
    /// sentinella iniziale coincide con quel valore. In pratica non esiste, ma
    /// vale la pena saperlo prima di scoprirlo.
    /// </summary>
    public const string ExpandAndWeigh = $"""
        SET NOCOUNT ON;

        DECLARE @page TABLE (OrderId UNIQUEIDENTIFIER PRIMARY KEY);
        DECLARE @Scanned INT, @Inserted INT;

        INSERT INTO @page (OrderId)
        SELECT TOP (@BatchSize) c.OrderId
        FROM Purge.RunCandidateOrder AS c
        WHERE c.RunId = @RunId
          AND c.State = 'Selected'
          AND c.OrderId > @LastId
        ORDER BY c.OrderId;

        SET @Scanned = @@ROWCOUNT;

        INSERT INTO Purge.RunCandidateOrderHistory (RunId, OrderHistoryId, OrderId)
        SELECT @RunId, oh.Id, oh.OrderRefId
        FROM {S}.OrderHistory AS oh
        INNER JOIN @page AS p ON p.OrderId = oh.OrderRefId
        WHERE NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrderHistory AS x
                          WHERE x.RunId = @RunId AND x.OrderHistoryId = oh.Id);

        SET @Inserted = @@ROWCOUNT;

        -- Peso in righe per ordine: testata + (storico testata + storico
        -- dettaglio). Il dettaglio corrente e' 1:1 e trascurabile ai fini del
        -- bilanciamento.
        UPDATE c
           SET c.RowWeight = 1 + ISNULL(h.Cnt, 0) * 2
        FROM Purge.RunCandidateOrder AS c
        INNER JOIN @page AS p ON p.OrderId = c.OrderId
        OUTER APPLY (
            SELECT Cnt = COUNT(*)
            FROM Purge.RunCandidateOrderHistory AS h
            WHERE h.RunId = c.RunId AND h.OrderId = c.OrderId
        ) AS h
        WHERE c.RunId = @RunId;

        SELECT Inserted   = @Inserted,
               Scanned    = @Scanned,
               NextAnchor = CAST(NULL AS DATETIME2),
               NextId     = (SELECT MAX(OrderId) FROM @page);
        """;

    /// <summary>Candidati ordinati per il bin packing streaming (§6.3).</summary>
    public const string ReadCandidatesForPlanning = """
        SELECT OrderId, RowWeight, CollectiveOrderId
        FROM Purge.RunCandidateOrder
        WHERE RunId = @RunId AND State = 'Selected'
        ORDER BY CASE WHEN CollectiveOrderId IS NULL THEN 0 ELSE 1 END, CollectiveOrderId, OrderId;
        """;

    public const string CreateOrphanAssignmentTempTable = """
        CREATE TABLE #assignOrphan (OrderHistoryId UNIQUEIDENTIFIER PRIMARY KEY,
                                    BatchNo INT NOT NULL);
        """;

    public const string ReadOrphansForPlanning = """
        SELECT OrderHistoryId
        FROM Purge.RunCandidateOrderHistory
        WHERE RunId = @RunId AND OrderId IS NULL AND BatchNo IS NULL
        ORDER BY OrderHistoryId;
        """;

    public const string ApplyOrphanAssignments = """
        UPDATE h
           SET h.BatchNo = a.BatchNo
        FROM Purge.RunCandidateOrderHistory AS h
        INNER JOIN #assignOrphan AS a ON a.OrderHistoryId = h.OrderHistoryId
        WHERE h.RunId = @RunId;
        """;

    public const string InitializeOrphanBatchProgress = """
        DELETE FROM Purge.RunBatchProgress WHERE RunId = @RunId;

        INSERT INTO Purge.RunBatchProgress
            (RunId, BatchNo, Status, IsOversized, OrderCount, EstimatedRowCount)
        SELECT @RunId, h.BatchNo, 'Pending', 0, COUNT(*), COUNT(*)
        FROM Purge.RunCandidateOrderHistory AS h
        WHERE h.RunId = @RunId AND h.OrderId IS NULL AND h.BatchNo IS NOT NULL
        GROUP BY h.BatchNo;
        """;

    public const string CreateAssignmentTempTable = """
        CREATE TABLE #assign (OrderId UNIQUEIDENTIFIER PRIMARY KEY,
                              BatchNo INT NOT NULL,
                              IsOversized BIT NOT NULL,
                              CollectiveOrderId UNIQUEIDENTIFIER NULL);
        """;

    public const string ApplyAssignments = """
        UPDATE c
           SET c.BatchNo = a.BatchNo, c.IsOversized = a.IsOversized, c.CollectiveOrderId = a.CollectiveOrderId
        FROM Purge.RunCandidateOrder AS c
        INNER JOIN #assign AS a ON a.OrderId = c.OrderId
        WHERE c.RunId = @RunId;

        UPDATE h
           SET h.BatchNo = c.BatchNo
        FROM Purge.RunCandidateOrderHistory AS h
        INNER JOIN Purge.RunCandidateOrder AS c
                ON c.OrderId = h.OrderId AND c.RunId = h.RunId
        WHERE h.RunId = @RunId;

        UPDATE rc
           SET rc.BatchNo = a.BatchNo
        FROM Purge.RunCandidateCollective AS rc
        INNER JOIN (
            SELECT CollectiveOrderId, BatchNo
            FROM #assign
            WHERE CollectiveOrderId IS NOT NULL
            GROUP BY CollectiveOrderId, BatchNo
        ) AS a ON a.CollectiveOrderId = rc.CollectiveOrderId
        WHERE rc.RunId = @RunId;
        """;

    /// <summary>
    /// Ripulisce prima di inserire: il planning viene rieseguito se il processo
    /// cade in quella fase, e senza questa DELETE la seconda esecuzione violerebbe
    /// la chiave primaria (RunId, BatchNo). E' sicura perche' il planning gira
    /// sempre prima che l'esecuzione cominci.
    /// </summary>
    public const string InitializeBatchProgress = """
        DELETE FROM Purge.RunBatchProgress WHERE RunId = @RunId;

        INSERT INTO Purge.RunBatchProgress
            (RunId, BatchNo, Status, IsOversized, OrderCount, EstimatedRowCount)
        SELECT @RunId, c.BatchNo, 'Pending',
               MAX(CAST(c.IsOversized AS INT)), COUNT(*), SUM(c.RowWeight)
        FROM Purge.RunCandidateOrder AS c
        WHERE c.RunId = @RunId AND c.State = 'Selected' AND c.BatchNo IS NOT NULL
        GROUP BY c.BatchNo;
        """;

    // =================================================================
    // FASE 3 — VALIDAZIONE (§7.3)
    // =================================================================

    /// <summary>V1 — caso C7: RefId verso un dettaglio fuori dal set candidati.</summary>
    public static string ValidateCrossReferences(DetailHistoryTable t) => $"""
        SELECT COUNT_BIG(*)
        FROM {S}.{t.Name} AS h
        INNER JOIN Purge.RunCandidateOrderHistory AS c
                ON c.OrderHistoryId = h.OrderHistoryId AND c.RunId = @RunId
        WHERE h.{t.RefColumn} IS NOT NULL
          AND NOT EXISTS (
                SELECT 1 FROM {S}.{t.CurrentTable} AS d
                INNER JOIN Purge.RunCandidateOrder AS co
                        ON co.OrderId = d.OrderId AND co.RunId = @RunId
                WHERE d.Id = h.{t.RefColumn});
        """;

    /// <summary>V2 — protezione modelli (C5): la cascata li distruggerebbe.</summary>
    public const string ValidateNoModelReference = $"""
        SELECT COUNT_BIG(*)
        FROM {S}.Model AS m
        INNER JOIN Purge.RunCandidateOrder AS c
                ON c.OrderId = m.OrderId AND c.RunId = @RunId
        WHERE c.State = 'Selected';
        """;

    /// <summary>V3 — coerenza collettivi: solo per le strategie non collettive.</summary>
    public const string ValidateNoCollectiveLink = $"""
        SELECT COUNT_BIG(*)
        FROM {S}.CollectiveOrderGroupOrder AS cgo
        INNER JOIN Purge.RunCandidateOrder AS c
                ON c.OrderId = cgo.OrderId AND c.RunId = @RunId
        WHERE c.State = 'Selected';
        """;

    /// <summary>V4 — rivalidazione dello stato fra selezione ed esecuzione.</summary>
    public static string ValidateStatusUnchanged(bool abandoned) => $"""
        SELECT COUNT_BIG(*)
        FROM {S}.[Order] AS o
        INNER JOIN Purge.RunCandidateOrder AS c
                ON c.OrderId = o.Id AND c.RunId = @RunId
        WHERE c.State = 'Selected'
          AND o.StatusCode NOT IN ({(abandoned
                ? "'Created','PartiallyAuthorised'"
                : TerminalStates)});
        """;

    /// <summary>V5 — copertura storici: sfuggirebbero alla slice e bloccherebbero Order.</summary>
    public const string ValidateHistoryCoverage = $"""
        SELECT COUNT_BIG(*)
        FROM {S}.OrderHistory AS oh
        INNER JOIN Purge.RunCandidateOrder AS c
                ON c.OrderId = oh.OrderRefId AND c.RunId = @RunId
        WHERE c.State = 'Selected'
          AND NOT EXISTS (SELECT 1 FROM Purge.RunCandidateOrderHistory AS h
                          WHERE h.RunId = @RunId AND h.OrderHistoryId = oh.Id);
        """;

    // =================================================================
    // FASE 4 — ESECUZIONE (§6.4)
    // =================================================================

    public static string DeleteDetailHistory(DetailHistoryTable t) => $"""
        DELETE h
        FROM {S}.{t.Name} AS h
        INNER JOIN Purge.RunCandidateOrderHistory AS c ON c.OrderHistoryId = h.OrderHistoryId
        WHERE c.RunId = @RunId AND c.BatchNo = @BatchNo;
        """;

    public const string DeleteOrderHistory = $"""
        DELETE oh
        FROM {S}.OrderHistory AS oh
        INNER JOIN Purge.RunCandidateOrderHistory AS c ON c.OrderHistoryId = oh.Id
        WHERE c.RunId = @RunId AND c.BatchNo = @BatchNo;
        """;

    public static string DeleteDetail(string table) => $"""
        DELETE d
        FROM {S}.{table} AS d
        INNER JOIN Purge.RunCandidateOrder AS c ON c.OrderId = d.OrderId
        WHERE c.RunId = @RunId AND c.BatchNo = @BatchNo;
        """;

    /// <summary>
    /// Testata, con rivalidazione dello stato nella stessa transazione (§7.4).
    /// Se il rowcount è inferiore agli ordini attesi per la slice, la
    /// transazione va sottoposta a rollback: procedere lascerebbe a database
    /// un ordine privo di storico e dettagli.
    /// </summary>
    public static string DeleteOrder(bool abandoned) => $"""
        DELETE o
        FROM {S}.[Order] AS o
        INNER JOIN Purge.RunCandidateOrder AS c ON c.OrderId = o.Id
        WHERE c.RunId = @RunId AND c.BatchNo = @BatchNo
          AND o.StatusCode IN ({(abandoned
                ? "'Created','PartiallyAuthorised'"
                : TerminalStates)});
        """;

    /// <summary>Storici orfani: nessun aggregato da preservare, set-based puro.</summary>
    public const string DeleteOrphanHistoryDetail = "{0}";

    public static string DeleteOrphanDetailHistory(DetailHistoryTable t) => $"""
        DELETE h
        FROM {S}.{t.Name} AS h
        INNER JOIN Purge.RunCandidateOrderHistory AS c
                ON c.OrderHistoryId = h.OrderHistoryId
        WHERE c.RunId = @RunId AND c.OrderId IS NULL AND c.BatchNo = @BatchNo;
        """;

    public const string DeleteOrphanOrderHistory = $"""
        DELETE oh
        FROM {S}.OrderHistory AS oh
        INNER JOIN Purge.RunCandidateOrderHistory AS c ON c.OrderHistoryId = oh.Id
        WHERE c.RunId = @RunId AND c.OrderId IS NULL AND c.BatchNo = @BatchNo;
        """;

    /// <summary>Difesa per run legacy: i Collective ancora Selected non dovrebbero esistere dopo una slice atomica.</summary>
    public const string ReadSelectedCollectives = """
        SELECT CollectiveOrderId
        FROM Purge.RunCandidateCollective
        WHERE RunId = @RunId AND State = 'Selected'
        ORDER BY CollectiveOrderId;
        """;

    public const string ValidateCollectiveComponentsCompleted = $"""
        SELECT COUNT(*)
        FROM Purge.RunCandidateOrder AS c
        INNER JOIN {S}.CollectiveOrderGroupOrder AS cgo ON cgo.OrderId = c.OrderId
        INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
        WHERE c.RunId = @RunId AND g.CollectiveOrderId = @CollectiveOrderId
          AND c.State <> 'Deleted';
        """;

    public const string MarkCollectiveFailed = """
        UPDATE Purge.RunCandidateCollective
           SET State = 'Failed', ExcludedReason = @Reason
         WHERE RunId = @RunId AND CollectiveOrderId = @CollectiveOrderId;
        """;

    public const string MarkCollectiveDeleted = """
        UPDATE Purge.RunCandidateCollective
           SET State = 'Deleted'
         WHERE RunId = @RunId AND CollectiveOrderId = @CollectiveOrderId AND State = 'Selected';
        """;

    /// <summary>
    /// Cancella la coda del Collective nella stessa transazione degli ordini componenti.
    /// Il parametro BatchNo identifica l'intera unita' atomica.
    /// </summary>
    public static IEnumerable<(string Table, string Sql)> CollectiveSliceStatements()
    {
        yield return ("CollectiveOrderGroupOrderHistoryResidue", $"""
            DELETE gh
            FROM {S}.CollectiveOrderGroupOrderHistory AS gh
            INNER JOIN {S}.CollectiveOrderGroupOrder AS cgo
                    ON cgo.Id = gh.CollectiveOrderGroupOrderRefId
            INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo;
            """);

        yield return ("CollectiveOrderGroupOrderResidue", $"""
            DELETE cgo
            FROM {S}.CollectiveOrderGroupOrder AS cgo
            INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo
              AND cgo.OrderId IS NULL;
            """);

        yield return ("CollectiveOrderGroupHistory", $"""
            DELETE gh
            FROM {S}.CollectiveOrderGroupHistory AS gh
            INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = gh.CollectiveOrderGroupRefId
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo;
            """);

        yield return ("CollectiveOrderGroup", $"""
            DELETE g
            FROM {S}.CollectiveOrderGroup AS g
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo;
            """);

        yield return ("CollectiveOrderContent", $"""
            DELETE c
            FROM {S}.CollectiveOrderContent AS c
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = c.CollectiveOrderId
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo;
            """);

        yield return ("CollectiveOrderHistory", $"""
            DELETE h
            FROM {S}.CollectiveOrderHistory AS h
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = h.CollectiveOrderRefId
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo;
            """);

        yield return ("CollectiveOrder", $"""
            DELETE co
            FROM {S}.CollectiveOrder AS co
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = co.Id
            WHERE rc.RunId = @RunId AND rc.BatchNo = @BatchNo;
            """);
    }

    /// <summary>
    /// Guardia eseguita nella stessa transazione della DELETE: ogni Collective
    /// del batch deve avere tutti i propri OrderId nello staging e tutti nello
    /// stesso BatchNo. Un valore > 0 forza il rollback prima di qualsiasi DELETE.
    /// </summary>
    public const string ValidateCollectiveBatchIntegrity = $"""
        SELECT
            InvalidCollectives =
                (SELECT COUNT(*)
                 FROM Purge.RunCandidateCollective AS rc
                 WHERE rc.RunId = @RunId
                   AND rc.BatchNo = @BatchNo
                   AND rc.State = 'Selected'
                   AND (
                        (SELECT COUNT(*)
                         FROM {S}.CollectiveOrderGroup AS g
                         INNER JOIN {S}.CollectiveOrderGroupOrder AS cgo
                                 ON cgo.CollectiveOrderGroupId = g.Id
                         WHERE g.CollectiveOrderId = rc.CollectiveOrderId
                           AND cgo.OrderId IS NOT NULL)
                        <>
                        (SELECT COUNT(*)
                         FROM Purge.RunCandidateOrder AS c
                         WHERE c.RunId = rc.RunId
                           AND c.CollectiveOrderId = rc.CollectiveOrderId)
                        OR EXISTS (
                            SELECT 1
                            FROM Purge.RunCandidateOrder AS c
                            WHERE c.RunId = rc.RunId
                              AND c.CollectiveOrderId = rc.CollectiveOrderId
                              AND (c.BatchNo <> rc.BatchNo OR c.BatchNo IS NULL)
                        )
                      ))
              + CASE WHEN EXISTS (
                    SELECT 1
                    FROM Purge.RunCandidateOrder AS legacy
                    WHERE legacy.RunId = @RunId
                      AND legacy.BatchNo = @BatchNo
                      AND legacy.CollectiveOrderId IS NULL
                ) THEN 1 ELSE 0 END;
        """;

    /// <summary>
    /// Traccia per tabella di cio' che la slice ha cancellato davvero.
    ///
    /// Scritta nella stessa transazione delle DELETE e del checkpoint. Se la
    /// slice va in rollback la traccia sparisce con essa, quindi non puo'
    /// esistere una riga di audit che dichiara righe ancora presenti a
    /// database — che e' l'unico modo in cui un audit puo' fare danno.
    ///
    /// Append-only, una riga per (RunId, BatchNo, tabella): nessun UPDATE, e
    /// quindi nessun punto di contesa se un giorno le slice verranno eseguite
    /// in parallelo. L'aggregazione per run la fa Purge.vDryRunVsActual.
    ///
    /// Un solo statement multi-riga anziche' uno per tabella: la transazione
    /// tiene lock sulle tabelle di dominio, e allungarla di venticinque
    /// round-trip per scrivere l'audit vanificherebbe il lavoro fatto sul
    /// dimensionamento delle slice.
    /// </summary>
    public static string InsertSliceAudit(int lineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineCount);

        var sb = new StringBuilder(
            "INSERT INTO Purge.PurgeAudit (RunId, BatchNo, TableName, RowsDeleted, RecordedOn)"
            + Environment.NewLine + "VALUES ");

        for (var i = 0; i < lineCount; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("(@RunId, @BatchNo, @T").Append(i)
              .Append(", @R").Append(i)
              .Append(", SYSDATETIMEOFFSET())");
        }

        return sb.Append(';').ToString();
    }

    public const string CheckpointSlice = """
        UPDATE Purge.RunBatchProgress
           SET Status = 'Completed', ActualDeletedRows = @RowsDeleted,
               CompletedOn = SYSDATETIMEOFFSET()
         WHERE RunId = @RunId AND BatchNo = @BatchNo;

        UPDATE Purge.RunCandidateOrder
           SET State = 'Deleted'
         WHERE RunId = @RunId AND BatchNo = @BatchNo AND State = 'Selected';

        UPDATE Purge.RunCandidateCollective
           SET State = 'Deleted'
         WHERE RunId = @RunId AND BatchNo = @BatchNo AND State = 'Selected';
        """;

    // =================================================================
    // DRY-RUN — conteggi senza cancellazione (§7.4)
    // =================================================================

    public static string CountDetailHistory(DetailHistoryTable t) => $"""
        SELECT COUNT_BIG(*)
        FROM {S}.{t.Name} AS h
        INNER JOIN Purge.RunCandidateOrderHistory AS c ON c.OrderHistoryId = h.OrderHistoryId
        WHERE c.RunId = @RunId;
        """;

    public const string CountOrderHistory = $"""
        SELECT COUNT_BIG(*)
        FROM {S}.OrderHistory AS oh
        INNER JOIN Purge.RunCandidateOrderHistory AS c ON c.OrderHistoryId = oh.Id
        WHERE c.RunId = @RunId;
        """;

    public static string CountDetail(string table) => $"""
        SELECT COUNT_BIG(*)
        FROM {S}.{table} AS d
        INNER JOIN Purge.RunCandidateOrder AS c ON c.OrderId = d.OrderId
        WHERE c.RunId = @RunId;
        """;

    public static string CountCollectiveAggregate(string table) => table switch
    {
        "CollectiveOrderGroupOrderHistoryResidue" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrderGroupOrderHistory AS gh
            INNER JOIN {S}.CollectiveOrderGroupOrder AS cgo
                    ON cgo.Id = gh.CollectiveOrderGroupOrderRefId
            INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId AND cgo.OrderId IS NULL;
            """,
        "CollectiveOrderGroupOrderResidue" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrderGroupOrder AS cgo
            INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = cgo.CollectiveOrderGroupId
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId AND cgo.OrderId IS NULL;
            """,
        "CollectiveOrderGroupHistory" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrderGroupHistory AS gh
            INNER JOIN {S}.CollectiveOrderGroup AS g ON g.Id = gh.CollectiveOrderGroupRefId
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId;
            """,
        "CollectiveOrderGroup" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrderGroup AS g
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = g.CollectiveOrderId
            WHERE rc.RunId = @RunId;
            """,
        "CollectiveOrderContent" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrderContent AS c
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = c.CollectiveOrderId
            WHERE rc.RunId = @RunId;
            """,
        "CollectiveOrderHistory" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrderHistory AS h
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = h.CollectiveOrderRefId
            WHERE rc.RunId = @RunId;
            """,
        "CollectiveOrder" => $"""
            SELECT COUNT_BIG(*)
            FROM {S}.CollectiveOrder AS co
            INNER JOIN Purge.RunCandidateCollective AS rc ON rc.CollectiveOrderId = co.Id
            WHERE rc.RunId = @RunId;
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Tabella collettiva sconosciuta.")
    };

    public const string CountOrder = """
        SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder
        WHERE RunId = @RunId AND State = 'Selected';
        """;

    /// <summary>
    /// Statistiche delle slice calcolate direttamente dalle assegnazioni dei
    /// candidati. Usata dal dry-run, che non crea Purge.RunBatchProgress.
    /// </summary>
    public const string DryRunSliceStatistics = """
        SELECT SliceCount = COUNT(*),
               MinRows    = ISNULL(MIN(EstimatedRowCount), 0),
               MaxRows    = ISNULL(MAX(EstimatedRowCount), 0),
               AvgRows    = ISNULL(AVG(EstimatedRowCount), 0),
               Oversized  = ISNULL(SUM(IsOversized), 0)
        FROM (
            SELECT c.BatchNo,
                   EstimatedRowCount = SUM(c.RowWeight),
                   IsOversized = MAX(CAST(c.IsOversized AS INT))
            FROM Purge.RunCandidateOrder AS c
            WHERE c.RunId = @RunId
              AND c.State = 'Selected'
              AND c.BatchNo IS NOT NULL
            GROUP BY c.BatchNo
        ) AS s;
        """;

    public const string DryRunOrphanSliceStatistics = """
        SELECT SliceCount = COUNT(*),
               MinRows    = ISNULL(MIN(EstimatedRowCount), 0),
               MaxRows    = ISNULL(MAX(EstimatedRowCount), 0),
               AvgRows    = ISNULL(AVG(EstimatedRowCount), 0),
               Oversized  = 0
        FROM (
            SELECT h.BatchNo,
                   EstimatedRowCount = COUNT(*)
            FROM Purge.RunCandidateOrderHistory AS h
            WHERE h.RunId = @RunId
              AND h.OrderId IS NULL
              AND h.BatchNo IS NOT NULL
            GROUP BY h.BatchNo
        ) AS s;
    """;

    public const string SliceStatistics = """
        SELECT SliceCount = COUNT(*),
               MinRows    = ISNULL(MIN(EstimatedRowCount), 0),
               MaxRows    = ISNULL(MAX(EstimatedRowCount), 0),
               AvgRows    = ISNULL(AVG(EstimatedRowCount), 0),
               Oversized  = SUM(CAST(IsOversized AS INT))
        FROM Purge.RunBatchProgress WHERE RunId = @RunId;
        """;

    public const string CountUnassigned = """
        SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder
        WHERE RunId = @RunId AND State = 'Selected' AND BatchNo IS NULL;
        """;

    // =================================================================
    // HOUSEKEEPING delle tabelle di controllo (v11 §7.4-7.6)
    // Lo staging cresce in proporzione ai dati cancellati: una riga per
    // ordine e una per revisione. Su volumi reali puo' superare in
    // dimensione i dati che ha contribuito a rimuovere.
    // =================================================================

    /// <summary>
    /// Run il cui staging e' eliminabile.
    ///
    /// Solo fasi terminali: cancellare lo staging di un run ancora riprendibile
    /// lo renderebbe irrecuperabile, perche' il set di candidati e' congelato e
    /// non viene mai ricalcolato.
    ///
    /// Due finestre distinte. I run conclusi senza incidenti si ripuliscono
    /// presto; quelli falliti o con slice abbandonate molto piu' tardi, perche'
    /// il loro staging e' l'unico appiglio per capire quali aggregati abbiano
    /// dato problemi.
    ///
    /// Il filtro su StagingPurgedOn colpisce l'indice filtrato creato da
    /// 005_housekeeping.sql, che contiene solo i run non ancora trattati.
    /// </summary>
    public const string SelectRunsToClean = """
        SELECT TOP (@MaxRuns) r.RunId, r.Strategy, r.Phase
        FROM Purge.PurgeRun AS r
        WHERE r.StagingPurgedOn IS NULL
          AND r.Phase IN ('Completed', 'Failed', 'Aborted')
          AND (
                (   r.Phase = 'Completed'
                AND r.CompletedOn IS NOT NULL
                AND r.CompletedOn < @CompletedCutoff
                AND NOT EXISTS (SELECT 1 FROM Purge.RunBatchProgress AS b
                                WHERE b.RunId = r.RunId AND b.Status = 'Abandoned'))
             OR r.StartedOn < @FailedCutoff
              )
        ORDER BY r.StartedOn;
        """;

    /// <summary>
    /// La chiave primaria clusterizzata delle tabelle di staging inizia con
    /// RunId: le righe di un run sono fisicamente contigue, quindi si tratta di
    /// una range delete e non di una scansione. Resta il ciclo a batch per non
    /// superare la soglia di lock escalation — il componente di sfoltimento
    /// deve sfoltire se stesso con la stessa cautela che applica altrove.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Sql)> HousekeepingStatements =
    [
        ("RunCandidateOrderHistory",
         "DELETE TOP (@BatchSize) FROM Purge.RunCandidateOrderHistory WHERE RunId = @RunId;"),
        ("RunCandidateOrder",
         "DELETE TOP (@BatchSize) FROM Purge.RunCandidateOrder WHERE RunId = @RunId;"),
        ("RunCandidateCollective",
         "DELETE TOP (@BatchSize) FROM Purge.RunCandidateCollective WHERE RunId = @RunId;"),
    ];

    /// <summary>
    /// Marca il run come ripulito. Va eseguita solo dopo che tutte e tre le
    /// tabelle sono state svuotate: un marcatore anticipato lascerebbe righe
    /// residue invisibili alle esecuzioni successive.
    /// </summary>
    public const string MarkStagingPurged = """
        UPDATE Purge.PurgeRun
           SET StagingPurgedOn = SYSDATETIMEOFFSET()
         WHERE RunId = @RunId;
        """;

    /// <summary>Staging residuo, per il log di riepilogo.</summary>
    public const string StagingFootprint = """
        SELECT RunConStaging = (SELECT COUNT(*) FROM Purge.PurgeRun WHERE StagingPurgedOn IS NULL),
               Ordini        = (SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrder),
               Storici       = (SELECT COUNT_BIG(*) FROM Purge.RunCandidateOrderHistory);
        """;

    public const string NextPendingSlice = """
        SELECT TOP (1) BatchNo, OrderCount, EstimatedRowCount, AttemptCount, IsOversized
        FROM Purge.RunBatchProgress
        WHERE RunId = @RunId AND Status IN ('Pending','Running')
        ORDER BY BatchNo;
        """;

    public static IEnumerable<(string Table, string Sql)> OrphanSliceStatements()
    {
        foreach (var t in PurgeTopology.DetailHistoryTables)
            yield return (t.Name, DeleteOrphanDetailHistory(t));
        yield return ("OrderHistory", DeleteOrphanOrderHistory);
    }

    /// <summary>Sequenza vincolante degli statement di una slice (§6.1).</summary>
    public static IEnumerable<(string Table, string Sql)> SliceStatements(bool abandoned)
    {
        foreach (var t in PurgeTopology.DetailHistoryTables)   // gruppo 1
            yield return (t.Name, DeleteDetailHistory(t));

        yield return ("OrderHistory", DeleteOrderHistory);      // gruppo 2

        foreach (var t in PurgeTopology.DetailTables)           // gruppo 3
            yield return (t, DeleteDetail(t));

        yield return ("Order", DeleteOrder(abandoned));         // gruppo 4
    }
}
