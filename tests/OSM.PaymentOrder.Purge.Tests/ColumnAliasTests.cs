using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Sql;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// D-14: le letture di produzione mappano per nome, quindi ogni colonna
/// calcolata deve avere un alias.
///
/// E' il rischio che il passaggio a Dapper introduce. Prima una colonna
/// aggiunta in mezzo spostava gli indici e rompeva le letture in modo
/// rumoroso; ora un alias mancante o rinominato non rompe niente: la
/// proprieta' resta al default del tipo. Un conteggio a zero e uno
/// StagingResiduo che riporta sempre zero sono guasti silenziosi.
///
/// sp_describe_first_result_set dice i nomi delle colonne senza eseguire lo
/// statement, quindi il controllo vale anche per i batch multi-statement
/// della selezione paginata, che non si possono eseguire a vuoto.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class ColumnAliasTests(PurgeDatabaseFixture db)
{
    private async Task AssertColumnsAsync(string sql, string parameterDeclarations, params string[] expected)
    {
        var names = await db.Sql.QueryAsync(
            "EXEC sp_describe_first_result_set @tsql = @Sql, @params = @Params, @browse_information_mode = 0;",
            r => r.GetString(r.GetOrdinal("name")),
            default,
            SqlParam.Of("@Sql", sql),
            SqlParam.Of("@Params", parameterDeclarations));

        Assert.Equal(expected, names);
    }

    [Fact]
    public Task Le_colonne_delle_slice_pendenti_hanno_i_nomi_attesi() =>
        AssertColumnsAsync(RetentionSql.NextPendingSlice, "@RunId uniqueidentifier",
            "BatchNo", "OrderCount", "EstimatedRowCount", "AttemptCount", "IsOversized", "SplitDepth");

    [Fact]
    public Task Le_colonne_dell_impronta_dello_staging_hanno_i_nomi_attesi() =>
        AssertColumnsAsync(RetentionSql.StagingFootprint, "",
            "RunConStaging", "Ordini", "Storici");

    [Fact]
    public Task Le_colonne_delle_statistiche_slice_hanno_i_nomi_attesi() =>
        AssertColumnsAsync(RetentionSql.DryRunSliceStatistics, "@RunId uniqueidentifier",
            "SliceCount", "MinRows", "MaxRows", "AvgRows", "Oversized");

    [Fact]
    public Task Le_colonne_delle_statistiche_orfani_hanno_i_nomi_attesi() =>
        AssertColumnsAsync(RetentionSql.DryRunOrphanSliceStatistics, "@RunId uniqueidentifier",
            "SliceCount", "MinRows", "MaxRows", "AvgRows", "Oversized");

    [Fact]
    public Task Le_colonne_dei_run_da_ripulire_hanno_i_nomi_attesi() =>
        AssertColumnsAsync(RetentionSql.SelectRunsToClean,
            "@MaxRuns int, @CompletedCutoff datetime2, @FailedCutoff datetime2",
            "RunId", "Strategy", "Phase");

    /// <summary>
    /// COUNT_BIG(*) senza alias non ha un nome su cui mappare: era il caso
    /// che ha reso necessario aggiungerne uno a questo statement.
    /// </summary>
    [Fact]
    public Task Le_colonne_dei_collettivi_esclusi_hanno_i_nomi_attesi() =>
        AssertColumnsAsync(RetentionSql.CountExcludedCollectivesByReason, "@RunId uniqueidentifier",
            "ExcludedReason", "Collectives");

    /// <summary>
    /// La riga di avanzamento della paginazione, per ogni statement paginato:
    /// e' la lettura piu' calda del motore e quella che si ripete a ogni pagina.
    /// SelectOrphanHistory restituisce NextId dalla propria chiave, che e' lo
    /// storico e non l'ordine, ma i nomi delle colonne sono gli stessi.
    /// </summary>
    [Theory]
    [MemberData(nameof(StatementPaginati))]
    public Task Le_colonne_di_avanzamento_hanno_i_nomi_attesi(string _, string sql, string parametri) =>
        AssertColumnsAsync(sql, parametri, "Inserted", "Scanned", "NextAnchor", "NextId");

    private const string ParametriPagina =
        "@RunId uniqueidentifier, @BatchSize int, " +
        "@LastAnchor datetime2, @LastId uniqueidentifier";

    private const string ParametriPaginaConCutoff = ParametriPagina + ", @Cutoff datetime2";

    public static TheoryData<string, string, string> StatementPaginati() => new()
    {
        { nameof(RetentionSql.SelectTerminated), RetentionSql.SelectTerminated, ParametriPaginaConCutoff },
        { nameof(RetentionSql.SelectStandingOrders), RetentionSql.SelectStandingOrders, ParametriPaginaConCutoff },
        { nameof(RetentionSql.SelectAbandoned), RetentionSql.SelectAbandoned, ParametriPaginaConCutoff },
        { nameof(RetentionSql.SelectOrphanHistory), RetentionSql.SelectOrphanHistory, ParametriPaginaConCutoff },
        { nameof(RetentionSql.ExpandAndWeigh), RetentionSql.ExpandAndWeigh, ParametriPagina }
    };
}
