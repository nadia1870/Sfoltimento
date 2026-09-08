using System.Data;
using Dapper;
using OSM.PaymentOrder.Purge.Data;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-14: la mappa globale DateTime -> datetime2.
///
/// E' l'unico punto in cui l'adozione di Dapper poteva rompere qualcosa di
/// silenzioso e grave: senza la mappa, un DateTime viaggia come
/// SqlDbType.DateTime, il cui minimo e' il 1753. La filigrana della
/// selezione paginata parte da DateTime.MinValue, e il valore verrebbe
/// rifiutato dal client prima ancora di arrivare al server.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DapperSetupTests
{
    /// <summary>
    /// Interroga la mappa dei tipi di Dapper, che e' esattamente cio' che
    /// DapperSetup modifica.
    ///
    /// LookupDbType e' pubblica ma dichiarata per uso interno, e la
    /// deprecazione va soppressa qui: e' l'unico modo di leggere la mappa
    /// senza un database. La via alternativa, far preparare a Dapper un
    /// comando vero e guardare il DbType del parametro, passa da
    /// DynamicParameters.AddParameters, che pretende una Identity: tipo
    /// altrettanto interno e non costruibile dall'esterno.
    ///
    /// Che il valore arrivi davvero al server lo prova DapperParameterTests,
    /// che manda DateTime.MinValue a SQL Server e rilegge cio' che torna.
    /// </summary>
    private static DbType TipoDiDefaultPer(Type tipo)
    {
        DapperSetup.Ensure();

#pragma warning disable CS0618 // uso interno: qui si verifica proprio la mappa interna
        var mappato = SqlMapper.LookupDbType(tipo, "V", demand: false, handler: out _);
#pragma warning restore CS0618

        // demand: false restituisce null per un tipo che Dapper non conosce.
        // Per i tipi di questo test non deve mai succedere, e se succede il
        // messaggio e' piu' utile di un InvalidCastException.
        return mappato ?? throw new InvalidOperationException(
            $"Dapper non ha un tipo di default per {tipo.Name}.");
    }

    [Fact]
    public void La_mappa_globale_manda_i_DateTime_come_datetime2()
    {
        // Senza la mappa sarebbe DbType.DateTime, il cui minimo e' il 1753:
        // la filigrana della selezione parte da DateTime.MinValue.
        Assert.Equal(DbType.DateTime2, TipoDiDefaultPer(typeof(DateTime)));
        Assert.Equal(DbType.DateTime2, TipoDiDefaultPer(typeof(DateTime?)));
    }

    /// <summary>
    /// Gli altri tipi non sono stati toccati: la mappa cambia solo i
    /// DateTime, e un effetto collaterale su Guid o DateTimeOffset sarebbe
    /// passato inosservato.
    /// </summary>
    [Fact]
    public void Gli_altri_tipi_restano_come_prima()
    {
        Assert.Equal(DbType.Guid, TipoDiDefaultPer(typeof(Guid)));
        Assert.Equal(DbType.DateTimeOffset, TipoDiDefaultPer(typeof(DateTimeOffset)));
        Assert.Equal(DbType.Int32, TipoDiDefaultPer(typeof(int)));
        Assert.Equal(DbType.String, TipoDiDefaultPer(typeof(string)));
    }

    /// <summary>Invocarla due volte non deve rompere niente: la chiama ogni SqlExecutor.</summary>
    [Fact]
    public void Applicarla_due_volte_e_innocuo()
    {
        DapperSetup.Ensure();
        DapperSetup.Ensure();

        Assert.Equal(DbType.DateTime2, TipoDiDefaultPer(typeof(DateTime)));
    }

    /// <summary>
    /// Il tipo esplicito resta necessario per i parametri NULL, il cui tipo
    /// non si deduce dal valore, e la conversione verso DbType deve coprire
    /// tutti i tipi che il motore usa davvero.
    /// </summary>
    [Fact]
    public void I_parametri_tipizzati_arrivano_a_Dapper_con_il_loro_tipo()
    {
        var dp = SqlParam.ToDapper(
        [
            SqlParam.Of("@RunId", Guid.NewGuid()),
            SqlParam.Typed("@Cutoff", DateTime.MinValue, SqlDbType.DateTime2),
            SqlParam.Typed("@Anchor", null, SqlDbType.DateTime2),
            SqlParam.Typed("@Id", null, SqlDbType.UniqueIdentifier),
            SqlParam.Typed("@Conteggio", 3L, SqlDbType.BigInt),
            SqlParam.Typed("@Nota", null, SqlDbType.NVarChar),
            SqlParam.Typed("@Flag", true, SqlDbType.Bit)
        ]);

        Assert.Equal(
            new[] { "RunId", "Cutoff", "Anchor", "Id", "Conteggio", "Nota", "Flag" },
            dp.ParameterNames);
    }

    [Fact]
    public void Uno_SqlDbType_non_mappato_solleva_con_il_nome_del_parametro()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlParam.ToDapper([SqlParam.Typed("@Blob", null, SqlDbType.VarBinary)]));

        Assert.Contains("@Blob", ex.Message);
    }
}
