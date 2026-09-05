namespace OSM.PaymentOrder.Purge.Sql;

/// <summary>
/// Tabella di storico di dettaglio: referenzia con Restrict SIA la riga di
/// OrderHistory SIA la riga di dettaglio corrente. Rif. v10 §3.2.
/// </summary>
public sealed record DetailHistoryTable(string Name, string RefColumn, string CurrentTable);

/// <summary>
/// Mappa topologica delle dipendenze di cancellazione. Rif. v10 §6.1.
///
/// L'ordine dei gruppi è il contratto di correttezza dell'intera soluzione.
/// Le undici tabelle del gruppo 1 sono le foglie del grafo: bloccano due rami
/// contemporaneamente e vanno per prime. NON riordinare senza aver riletto §3.2.
/// </summary>
public static class PurgeTopology
{
    public const string Schema = "PaymentOrder";

    /// <summary>Gruppo 1 — storici di dettaglio. Join su RunCandidateOrderHistory.</summary>
    public static readonly IReadOnlyList<DetailHistoryTable> DetailHistoryTables =
    [
        new("AccountTransferHistory",           "AccountTransferRefId",           "AccountTransfer"),
        new("BankTransferHistory",              "BankTransferRefId",              "BankTransfer"),
        new("BankTransferToCornerCardHistory",  "BankTransferToCornerCardRefId",  "BankTransferToCornerCard"),
        new("ForeignBankTransferHistory",       "ForeignBankTransferRefId",       "ForeignBankTransfer"),
        new("InpaymentSlipHistory",             "InpaymentSlipRefId",             "InpaymentSlip"),
        new("IpBankTransferHistory",            "IpBankTransferRefId",            "IpBankTransfer"),
        new("IpQRBillHistory",                  "IpQRBillRefId",                  "IpQRBill"),
        new("QRBillHistory",                    "QRBillRefId",                    "QRBill"),
        new("RealTimeCardReloadHistory",        "RealTimeCardReloadRefId",        "RealTimeCardReload"),
        new("StandingOrderHistory",             "StandingOrderRefId",             "StandingOrder"),
        new("CollectiveOrderGroupOrderHistory", "CollectiveOrderGroupOrderRefId", "CollectiveOrderGroupOrder"),
    ];

    /// <summary>Gruppo 3 — dettagli correnti. Join su RunCandidateOrder via OrderId.</summary>
    public static readonly IReadOnlyList<string> DetailTables =
    [
        "AccountTransfer",
        "BankTransfer",
        "BankTransferToCornerCard",
        "ForeignBankTransfer",
        "InpaymentSlip",
        "IpBankTransfer",
        "IpQRBill",
        "QRBill",
        "RealTimeCardReload",
        "StandingOrder",
        "CollectiveOrderGroupOrder",
        // Model: cancellazione esplicita anziche' per cascata (C5), per
        // determinismo e per poterla conteggiare nel report. I candidati
        // referenziati da Model sono comunque esclusi in selezione.
        "Model",
    ];

    /// <summary>
    /// Tabelle di coda del Collective. Nei nuovi run vengono eseguite nella
    /// stessa slice degli ordini componenti; non costituiscono piu' una fase
    /// transazionale separata.
    /// </summary>
    public static readonly IReadOnlyList<string> CollectiveTailTables =
    [
        // Residui delle righe scartate in validazione (C3): non passano dallo
        // staging perche' non hanno OrderId, e bloccherebbero CollectiveOrderGroup.
        "CollectiveOrderGroupOrderHistoryResidue",
        "CollectiveOrderGroupOrderResidue",
        "CollectiveOrderGroupHistory",
        "CollectiveOrderGroup",
        "CollectiveOrderContent",
        "CollectiveOrderHistory",
        "CollectiveOrder",
    ];

    /// <summary>Elenco piatto delle tabelle toccate, per audit e dry-run.</summary>
    public static IEnumerable<string> AllOrderScopedTables() =>
        DetailHistoryTables.Select(t => t.Name)
            .Concat(["OrderHistory"])
            .Concat(DetailTables)
            .Concat(["Order"]);
}
