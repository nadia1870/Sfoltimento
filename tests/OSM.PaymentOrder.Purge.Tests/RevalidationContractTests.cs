using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Fissa l'invariante su cui si regge la rivalidazione in transazione.
///
/// SliceExecutor confronta il numero di testate cancellate con quello atteso e
/// annulla se non coincidono. Il confronto sul conteggio equivale a un
/// confronto sull'insieme, ma solo perche' tre cose sono vere insieme:
///
///   1. Purge.RunCandidateOrder ha chiave primaria (RunId, OrderId): l'elenco
///      e' congelato e senza duplicati, quindi il conteggio non puo' gonfiarsi;
///   2. la DELETE fa join su quell'elenco: non puo' toccare un ordine che non
///      vi appartiene, quindi l'identita' non puo' derivare;
///   3. il predicato di stato e' DENTRO la DELETE, non in un controllo
///      precedente: un ordine uscito dallo stato terminale non viene
///      cancellato e il conteggio scende.
///
/// Le tre condizioni stanno in tre file diversi e nessuna dichiara di reggere
/// le altre. Basta togliere il predicato di stato dalla DELETE — sembra
/// ridondante, l'elenco e' gia' filtrato in selezione — e la rivalidazione
/// smette silenziosamente di rivalidare: gli ordini tornati in lavorazione
/// verrebbero cancellati e il conteggio tornerebbe.
///
/// Questi test falliscono se una delle tre viene rimossa.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RevalidationContractTests
{
    [Fact]
    public void La_delete_su_Order_filtra_sullo_stato()
    {
        var sql = RetentionSql.DeleteOrder(abandoned: false);

        Assert.Contains("o.StatusCode IN (", sql);
        foreach (var stato in new[] { "Executed", "Cancelled", "Deleted", "Refused", "Extincted" })
            Assert.Contains($"'{stato}'", sql);
    }

    [Fact]
    public void La_delete_degli_abbandonati_filtra_sui_propri_stati()
    {
        var sql = RetentionSql.DeleteOrder(abandoned: true);

        Assert.Contains("'Created','PartiallyAuthorised'", sql);
        Assert.DoesNotContain("'Executed'", sql);
    }

    /// <summary>
    /// La DELETE tocca solo gli ordini dell'elenco congelato, filtrati per run
    /// e per slice. Senza il join l'identita' potrebbe derivare e il conteggio
    /// non direbbe piu' nulla.
    /// </summary>
    [Fact]
    public void La_delete_su_Order_e_vincolata_all_elenco_congelato()
    {
        var sql = RetentionSql.DeleteOrder(abandoned: false);

        Assert.Contains("INNER JOIN Purge.RunCandidateOrder", sql);
        Assert.Contains("c.RunId = @RunId", sql);
        Assert.Contains("c.BatchNo = @BatchNo", sql);
    }

    /// <summary>
    /// L'unicita' del candidato dentro il run e' l'ultima delle tre gambe: se
    /// lo stesso OrderId potesse comparire due volte, la DELETE ne cancellerebbe
    /// uno solo e il conteggio non tornerebbe mai — oppure, con un join che
    /// duplica, tornerebbe per la ragione sbagliata.
    /// </summary>
    [Fact]
    public void Lo_staging_dichiara_l_unicita_del_candidato_nel_run()
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "000_install_purge.sql"));

        Assert.Contains("PK_RunCandidateOrder PRIMARY KEY CLUSTERED (RunId, OrderId)", schema);
    }
}
