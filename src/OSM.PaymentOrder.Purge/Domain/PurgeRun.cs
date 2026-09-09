namespace OSM.PaymentOrder.Purge.Domain;

/// <summary>Fasi del run. Rif. v10 §7.2.</summary>
public enum RunPhase
{
    Created, Selecting, Expanding, Validating, Planning, Executing,
    Completed, CompletedWithErrors, Failed, Aborted
}

/// <summary>
/// Cosa significa che un run e' finito.
///
/// L'elenco delle fasi terminali era ripetuto in quattro punti — orchestratore,
/// PhaseResult, ricerca dei run riprendibili, housekeeping — e ogni fase nuova
/// richiedeva di trovarli tutti. Sta qui una volta sola.
/// </summary>
public static class RunPhases
{
    /// <summary>Fasi da cui un run non riparte. Nessun IPurgePhase le gestisce.</summary>
    public static bool IsTerminal(RunPhase phase) =>
        phase is RunPhase.Completed
              or RunPhase.CompletedWithErrors
              or RunPhase.Failed
              or RunPhase.Aborted;

    /// <summary>
    /// Nomi delle fasi terminali per gli statement SQL. Derivati dall'enum, non
    /// riscritti a mano: una fase aggiunta al tipo compare da sola nelle query.
    /// </summary>
    public static readonly IReadOnlyList<string> TerminalNames =
        Enum.GetValues<RunPhase>().Where(IsTerminal).Select(p => p.ToString()).ToList();

    /// <summary>Lista pronta per una clausola IN, gia' quotata.</summary>
    public static string TerminalSqlList =>
        string.Join(",", TerminalNames.Select(n => $"'{n}'"));
}

/// <summary>
/// Strategie del solo flusso di retention. Gli scenari on-demand
/// (numero relazione, PaymentId) non sono implementati qui.
/// </summary>
public enum RetentionStrategy
{
    /// <summary>Ordini in stato terminale oltre la soglia (§10.4).</summary>
    Terminated,

    /// <summary>Ordini mai autorizzati, ancoraggio su CreationDate (§10.5).</summary>
    Abandoned,

    /// <summary>Piani ricorrenti terminati (§10.6).</summary>
    StandingOrders,

    /// <summary>Aggregati collettivi, unità atomica (§10.7).</summary>
    Collective,

    /// <summary>Storici con OrderRefId NULL, caso C1.</summary>
    OrphanHistory
}

/// <summary>
/// Come si calcola la soglia. La scelta è normativa, non tecnica (PA-3):
/// lo scarto fra le due letture arriva a dodici mesi.
/// </summary>
public enum RetentionAnchorMode
{
    /// <summary>Data mobile: cutoff = oggi - N anni.</summary>
    RollingDate,

    /// <summary>
    /// Chiusura d'esercizio: cutoff = 1 gennaio dell'anno (corrente - N).
    /// Lettura prudente: conserva di più, e l'errore che produce è recuperabile.
    /// </summary>
    FiscalYearEnd
}

/// <summary>Stato di un'esecuzione. I parametri sono congelati alla creazione (§7.2).</summary>
public sealed class PurgeRun
{
    public required Guid RunId { get; init; }
    public required RetentionStrategy Strategy { get; init; }
    public RunPhase Phase { get; set; }
    public required bool DryRun { get; init; }
    public required RetentionAnchorMode AnchorMode { get; init; }

    /// <summary>Soglia per gli ordini conclusi, ricorrenti e collettivi.</summary>
    public required DateTime RetentionCutoff { get; init; }

    /// <summary>Soglia distinta per gli ordini abbandonati (§10.5).</summary>
    public DateTime? AbandonedCutoff { get; init; }

    public required int MaxRowsPerBatch { get; init; }
    public required int MaxOrdersPerBatch { get; init; }

    /// <summary>
    /// Quante volte il run e' stato interrotto da un guasto e ripreso.
    ///
    /// Un guasto non chiude il run: la fase resta dov'e', perche' e' il
    /// checkpoint da cui riprendere. Senza un contatore pero' un problema
    /// stabile — un disco pieno, una rete rotta — farebbe ripartire lo stesso
    /// run ogni notte all'infinito, sempre con lo stesso esito.
    /// </summary>
    public int InterruptionCount { get; set; }

    // La scelta fra RetentionCutoff e AbandonedCutoff appartiene alla strategia
    // (IPurgeStrategy.CutoffOf) e non al modello del run: era l'ultimo switch
    // sull'enum rimasto fuori dal refactor.
}

/// <summary>Una slice di aggregati, unità di transazione della retention (§6.2).</summary>
public sealed class SliceInfo
{
    public required int BatchNo { get; init; }
    public required int OrderCount { get; init; }
    public required int EstimatedRowCount { get; init; }
    public required int AttemptCount { get; init; }

    /// <summary>
    /// Aggregato il cui peso individuale supera MaxRowsPerBatch: transazione
    /// dedicata, con un picco di lock accettato consapevolmente (§6.3).
    /// </summary>
    public required bool IsOversized { get; init; }

    /// <summary>
    /// Quante bisezioni separano questa slice da quella pianificata in
    /// origine (D-11). Zero per le slice del planner. Oltre MaxSplitDepth
    /// il coordinatore smette di dividere e abbandona.
    /// </summary>
    public int SplitDepth { get; init; }
}

public enum SliceOutcome { Completed, Retryable, Fatal }

public sealed class SliceResult
{
    public required SliceOutcome Outcome { get; init; }
    public int RowsDeleted { get; init; }
    public string? Reason { get; init; }

    /// <summary>
    /// Vero se il guasto e' un errore di dati circoscritto a un aggregato —
    /// una FK violata, una chiave duplicata — e quindi dividere la slice in
    /// due puo' isolarlo (D-11). Falso per tutto il resto: un difetto del
    /// programma fallirebbe ogni figlia allo stesso modo, e dividere
    /// moltiplicherebbe le transazioni fallite senza scoprire niente.
    /// </summary>
    public bool Splittable { get; init; }

    public static SliceResult Ok(int rows) =>
        new() { Outcome = SliceOutcome.Completed, RowsDeleted = rows };

    public static SliceResult Retryable(string reason) =>
        new() { Outcome = SliceOutcome.Retryable, Reason = reason };

    public static SliceResult Fatal(string reason, bool splittable = false) =>
        new() { Outcome = SliceOutcome.Fatal, Reason = reason, Splittable = splittable };
}

public sealed record ValidationFinding(string RuleId, string Table, long AffectedCount);

/// <summary>Esito delle validazioni pre-esecuzione (§7.3).</summary>
public sealed class ValidationReport
{
    private readonly List<ValidationFinding> _findings = [];

    public IReadOnlyList<ValidationFinding> Findings => _findings;

    public void Add(string ruleId, string table, long affected)
    {
        if (affected > 0) _findings.Add(new ValidationFinding(ruleId, table, affected));
    }

    /// <summary>
    /// Qualunque finding è bloccante. Con transazioni atomiche un'anomalia non
    /// corrompe più nulla, ma un problema sistematico non intercettato qui si
    /// tradurrebbe nell'abbandono di massa delle slice.
    /// </summary>
    public bool HasBlockingIssues => _findings.Count > 0;

    public string FailedRules => string.Join(",", _findings.Select(f => f.RuleId).Distinct());

    public long TotalAffected => _findings.Sum(f => f.AffectedCount);
}

/// <summary>Report del dry-run (§7.4).</summary>
public sealed class DryRunReport
{
    private readonly Dictionary<string, long> _lines = [];
    private readonly Dictionary<string, long> _excludedCollectives = [];

    public required Guid RunId { get; init; }
    public DateTimeOffset ProducedOn { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, long> Lines => _lines;

    public int SliceCount { get; set; }
    public int OversizedCount { get; set; }
    public int MinRowsPerSlice { get; set; }
    public int MaxRowsPerSlice { get; set; }
    public int AvgRowsPerSlice { get; set; }
    public long UnassignedOrders { get; set; }

    public void Add(string table, long count) => _lines[table] = count;

    /// <summary>
    /// Collettivi censiti e non cancellati, per motivo (D-10). Non entrano in
    /// Lines: quelle righe vengono persistite in Purge.DryRunReport e
    /// confrontate con l'audit per nome di tabella, e un motivo di esclusione
    /// non e' una tabella. Comparirebbero in vDryRunVsActual come scostamento.
    /// </summary>
    public IReadOnlyDictionary<string, long> ExcludedCollectives => _excludedCollectives;

    public void AddExcludedCollectives(string reason, long count) =>
        _excludedCollectives[reason] = count;

    /// <summary>
    /// Stati non riconosciuti come terminali, con quanti ordini oltre soglia
    /// li portano. Vuoto e' l'esito normale. Non entra in Lines per la stessa
    /// ragione dei collettivi esclusi: non sono nomi di tabella.
    /// </summary>
    public IReadOnlyList<(string StatusCode, long Count, DateTime? Oldest)> UnknownStatuses =>
        _unknownStatuses;

    private readonly List<(string StatusCode, long Count, DateTime? Oldest)> _unknownStatuses = [];

    public void AddUnknownStatus(string statusCode, long count, DateTime? oldest) =>
        _unknownStatuses.Add((statusCode, count, oldest));

    public long OrderCount => _lines.GetValueOrDefault("Order");
    public long TotalRows => _lines.Values.Sum();

    /// <summary>Vero se una slice non oversized sfora il tetto: bin packing da rivedere.</summary>
    public bool ExceedsRowBudget(int maxRowsPerBatch) =>
        OversizedCount == 0 && MaxRowsPerSlice > maxRowsPerBatch;

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Dry-run {RunId} — {ProducedOn:u}");
        sb.AppendLine(new string('-', 54));
        foreach (var (table, count) in _lines.OrderByDescending(l => l.Value))
            sb.AppendLine($"{table,-36}{count,14:N0}");
        sb.AppendLine(new string('-', 54));
        sb.AppendLine($"{"TOTALE RIGHE",-36}{TotalRows,14:N0}");
        sb.AppendLine();
        sb.AppendLine($"Slice: {SliceCount:N0}   righe/slice min={MinRowsPerSlice} " +
                      $"avg={AvgRowsPerSlice} max={MaxRowsPerSlice}");
        sb.AppendLine($"Aggregati oversized: {OversizedCount:N0}");
        if (_excludedCollectives.Count > 0)
        {
            sb.AppendLine("Collettivi esclusi e censiti (non verranno cancellati):");
            foreach (var (reason, count) in _excludedCollectives.OrderBy(e => e.Key))
                sb.AppendLine($"  {reason,-34}{count,14:N0}");
        }
        if (_unknownStatuses.Count > 0)
        {
            sb.AppendLine("Stati non riconosciuti come terminali, oltre soglia:");
            foreach (var (status, count, oldest) in _unknownStatuses)
                sb.AppendLine($"  {status,-34}{count,14:N0}   dal {oldest:yyyy-MM-dd}");
            sb.AppendLine("  ^ questi ordini non verranno mai sfoltiti finche' lo stato");
            sb.AppendLine("    non viene aggiunto a RetentionSql.TerminalStates. Verificare");
            sb.AppendLine("    con il referente applicativo se sono stati conclusivi.");
        }
        if (UnassignedOrders > 0)
            sb.AppendLine($"ATTENZIONE: {UnassignedOrders:N0} ordini senza BatchNo");
        return sb.ToString();
    }
}
