using OSM.PaymentOrder.Purge.Engine;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-16: il tetto per aggregato.
///
/// MaxRowsPerBatch limita la slice, ma un aggregato che lo supera da solo
/// diventa una slice dedicata — finora senza limite superiore. Un collettivo
/// da duecento componenti con molte revisioni puo' pesare centomila righe: una
/// transazione che innesca la lock escalation e gonfia il log, cioe' cio' che
/// il packing esiste per evitare.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AggregateWeightCeilingTests
{
    private static BatchPacker.Candidate Ordine(int weight, Guid? collective = null) =>
        new(Guid.NewGuid(), weight, collective);

    [Fact]
    public void Senza_tetto_un_aggregato_enorme_resta_una_slice_dedicata()
    {
        var packer = new BatchPacker(maxRowsPerBatch: 100, maxOrdersPerBatch: 10);

        var a = Assert.Single(packer.Add(Ordine(100_000)));

        Assert.False(a.Excluded);
        Assert.True(a.IsOversized);
        Assert.Equal(0, packer.TooLargeCount);
    }

    [Fact]
    public void Sopra_il_tetto_l_ordine_viene_escluso_e_non_assegnato()
    {
        var packer = new BatchPacker(maxRowsPerBatch: 100, maxOrdersPerBatch: 10, maxAggregateWeight: 1000);

        var a = Assert.Single(packer.Add(Ordine(1001)));

        Assert.True(a.Excluded);
        Assert.False(a.IsOversized);
        Assert.Equal(1, packer.TooLargeCount);
    }

    [Fact]
    public void Sul_tetto_esatto_non_viene_escluso()
    {
        var packer = new BatchPacker(maxRowsPerBatch: 100, maxOrdersPerBatch: 10, maxAggregateWeight: 1000);

        Assert.False(Assert.Single(packer.Add(Ordine(1000))).Excluded);
        Assert.Equal(0, packer.TooLargeCount);
    }

    /// <summary>
    /// Il collettivo si misura sul peso complessivo dei componenti, non su
    /// quello del singolo: e' l'aggregato a finire in una transazione.
    /// </summary>
    [Fact]
    public void Un_collettivo_si_misura_intero_e_viene_escluso_intero()
    {
        var packer = new BatchPacker(maxRowsPerBatch: 100, maxOrdersPerBatch: 50, maxAggregateWeight: 1000);
        var collettivo = Guid.NewGuid();

        for (var i = 0; i < 3; i++) packer.Add(Ordine(400, collettivo));
        var assegnazioni = packer.Complete();

        Assert.Equal(3, assegnazioni.Count);
        Assert.All(assegnazioni, a =>
        {
            Assert.True(a.Excluded);
            Assert.Equal(collettivo, a.CollectiveOrderId);
        });
        Assert.Equal(1, packer.TooLargeCount);
    }

    /// <summary>
    /// L'esclusione non interrompe il resto: gli aggregati normali che
    /// seguono continuano a essere impacchettati.
    /// </summary>
    [Fact]
    public void Un_aggregato_escluso_non_disturba_quelli_dopo()
    {
        var packer = new BatchPacker(maxRowsPerBatch: 100, maxOrdersPerBatch: 10, maxAggregateWeight: 1000);

        packer.Add(Ordine(20));
        packer.Add(Ordine(5000));
        packer.Add(Ordine(20));
        packer.Complete();

        Assert.Equal(1, packer.TooLargeCount);
        Assert.Equal(3, packer.Total);
    }

    /// <summary>
    /// Un tetto sotto MaxRowsPerBatch renderebbe indecidibile il caso
    /// intermedio: un aggregato dovrebbe essere insieme slice dedicata ed
    /// escluso. Meglio rifiutare la configurazione all'avvio.
    /// </summary>
    [Fact]
    public void Un_tetto_sotto_il_limite_di_slice_e_rifiutato()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatchPacker(maxRowsPerBatch: 3000, maxOrdersPerBatch: 500, maxAggregateWeight: 100));
    }
}
