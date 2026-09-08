using System.Data;
using OSM.PaymentOrder.Purge.Data;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// La mappa datetime2 sul database vero (D-14): e' il caso che nessun test
/// unitario puo' provare, perche' il rifiuto avviene nel client di
/// Microsoft.Data.SqlClient al momento di serializzare il parametro.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class DapperParameterTests(PurgeDatabaseFixture db)
{
    /// <summary>
    /// DateTime.MinValue e' la filigrana da cui parte ogni selezione paginata.
    /// Senza la mappa globale questa chiamata solleverebbe prima di
    /// raggiungere SQL Server.
    /// </summary>
    [Fact]
    public async Task La_filigrana_iniziale_arriva_al_server_intatta()
    {
        var eco = await db.Sql.ScalarAsync<DateTime>(
            "SELECT @Anchor;", default, SqlParam.Of("@Anchor", DateTime.MinValue));

        Assert.Equal(DateTime.MinValue, eco);
    }

    /// <summary>Vale anche senza tipo dichiarato: e' il senso della mappa globale.</summary>
    [Fact]
    public async Task Un_DateTime_prima_del_1753_non_richiede_il_tipo_esplicito()
    {
        var senzaTipo = await db.Sql.ScalarAsync<DateTime>(
            "SELECT @D;", default, SqlParam.Of("@D", new DateTime(1600, 1, 1)));

        var conTipo = await db.Sql.ScalarAsync<DateTime>(
            "SELECT @D;", default, SqlParam.Typed("@D", new DateTime(1600, 1, 1), SqlDbType.DateTime2));

        Assert.Equal(new DateTime(1600, 1, 1), senzaTipo);
        Assert.Equal(senzaTipo, conTipo);
    }

    /// <summary>
    /// Un parametro NULL conserva il tipo dichiarato: e' il caso per cui
    /// SqlParam.Typed resta necessario anche dopo la mappa globale.
    /// </summary>
    [Fact]
    public async Task Un_parametro_nullo_tipizzato_resta_leggibile()
    {
        var eco = await db.Sql.ScalarAsync<int>(
            "SELECT CASE WHEN @Anchor IS NULL THEN 1 ELSE 0 END;", default,
            SqlParam.Typed("@Anchor", null, SqlDbType.DateTime2));

        Assert.Equal(1, eco);
    }

    /// <summary>
    /// Lo scalare tipizzato conserva il comportamento precedente: NULL dal
    /// server diventa default(T), non un'eccezione.
    /// </summary>
    [Fact]
    public async Task Uno_scalare_nullo_diventa_il_default_del_tipo()
    {
        Assert.Equal(0L, await db.Sql.ScalarAsync<long>("SELECT CAST(NULL AS BIGINT);", default));
        Assert.Null(await db.Sql.ScalarAsync<string>("SELECT CAST(NULL AS NVARCHAR(10));", default));
    }

    /// <summary>
    /// Anche dentro una sessione transazionale, che e' il percorso delle
    /// slice: stessa mappa, stessa conversione degli scalari.
    /// </summary>
    [Fact]
    public async Task La_sessione_transazionale_si_comporta_allo_stesso_modo()
    {
        await using var session = await db.Sql.BeginSessionAsync(default);

        var eco = await session.ScalarAsync<DateTime>(
            "SELECT @Anchor;", default, SqlParam.Of("@Anchor", DateTime.MinValue));

        // Il conteggio delle righe e' cio' su cui si regge la rivalidazione
        // in SliceExecutor: deve restare quello di ExecuteNonQuery.
        var righe = await session.ExecuteAsync(
            "DELETE FROM Purge.PurgeAudit WHERE RunId = @RunId;", default,
            SqlParam.Of("@RunId", Guid.NewGuid()));

        Assert.Equal(DateTime.MinValue, eco);
        Assert.Equal(0, righe);

        await session.RollbackAsync(default);
    }
}
