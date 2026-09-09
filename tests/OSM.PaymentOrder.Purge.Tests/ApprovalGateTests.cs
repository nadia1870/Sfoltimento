using Microsoft.Extensions.DependencyInjection;
using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Secondo livello: la modalita' Delete richiede un'approvazione della policy
/// registrata su questo database.
///
/// Copre il caso che la modalita' esplicita da sola non copre: qualcuno
/// pianifica il job DELETE senza che nessuno abbia esaminato un report.
/// </summary>
[Trait("Category", "Integration")]
[Collection("PurgeDatabase")]
public sealed class ApprovalGateTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private PurgeApprovalGate Gate => db.Services.GetRequiredService<PurgeApprovalGate>();

    private Task ApprovaPolicyCorrenteAsync(string by = "operatore") =>
        db.Store.ApproveAsync(
            PurgePolicy.ComputeHash(db.Options), Guid.NewGuid(), by,
            PurgePolicy.Describe(db.Options), null, default);

    /// <summary>
    /// Un dry-run non ha bisogno di approvazione: non cancella niente, ed
    /// e' anzi il modo per ottenerla.
    /// </summary>
    [Fact]
    public async Task Il_dry_run_non_richiede_approvazione() =>
        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.DryRun, default));

    /// <summary>
    /// Il caso che il gate esiste per intercettare: database senza
    /// approvazioni, modalita' Delete, rifiuto.
    /// </summary>
    [Fact]
    public async Task Delete_senza_approvazione_viene_rifiutato() =>
        Assert.False(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));

    [Fact]
    public async Task Delete_con_la_policy_approvata_procede()
    {
        await ApprovaPolicyCorrenteAsync();

        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
    }

    /// <summary>
    /// Il punto che rende il gate utile a regime e non solo la prima notte:
    /// se qualcuno porta la retention da cinque anni a due dopo
    /// l'approvazione, l'impronta non corrisponde piu' e si riparte da un
    /// dry-run.
    /// </summary>
    [Fact]
    public async Task Cambiare_la_policy_invalida_l_approvazione()
    {
        await ApprovaPolicyCorrenteAsync();
        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));

        var originale = db.Options.RetentionYears;
        try
        {
            db.Options.RetentionYears = originale - 3;
            Assert.False(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
        }
        finally
        {
            db.Options.RetentionYears = originale;
        }
    }

    /// <summary>
    /// D-19: alzare il tetto per aggregato invalida l'approvazione.
    ///
    /// E' il caso end-to-end del ragionamento di D-19. Si approva con il tetto
    /// a 30 000 e il report mostra un collettivo da 80 000 fra gli esclusi:
    /// chi approva sta approvando anche quell'esclusione. Alzando il tetto,
    /// quell'aggregato diventerebbe cancellabile con un'approvazione che
    /// nessuno ha dato per lui, e il gate deve accorgersene.
    ///
    /// Il test sull'impronta in ExecutionModeTests verifica la stessa cosa
    /// sull'aritmetica; questo la verifica sul percorso reale, che e' quello
    /// che decide se una cancellazione parte.
    /// </summary>
    [Fact]
    public async Task Alzare_il_tetto_per_aggregato_invalida_l_approvazione()
    {
        await ApprovaPolicyCorrenteAsync();
        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));

        var originale = db.Options.MaxAggregateWeight;
        try
        {
            db.Options.MaxAggregateWeight = originale == 0 ? 30_000 : originale * 3;
            Assert.False(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
        }
        finally
        {
            db.Options.MaxAggregateWeight = originale;
        }

        // Tornando al valore approvato l'approvazione riprende a valere: e'
        // la policy a essere approvata, non un momento nel tempo.
        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
    }

    /// <summary>
    /// Tarare le manopole di prestazione non deve bloccare il job notturno:
    /// e' il falso allarme che farebbe disattivare il controllo.
    /// </summary>
    [Fact]
    public async Task Cambiare_le_manopole_di_prestazione_non_invalida_l_approvazione()
    {
        await ApprovaPolicyCorrenteAsync();

        var originale = db.Options.SelectionBatchSize;
        try
        {
            db.Options.SelectionBatchSize = originale * 3;
            Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
        }
        finally
        {
            db.Options.SelectionBatchSize = originale;
        }
    }

    /// <summary>
    /// Riapprovare sovrascrive: chi ha esaminato per ultimo e' chi risponde.
    /// </summary>
    [Fact]
    public async Task Riapprovare_aggiorna_chi_risponde()
    {
        await ApprovaPolicyCorrenteAsync("primo");
        await ApprovaPolicyCorrenteAsync("secondo");

        var approvazione = await db.Store.FindApprovalAsync(
            PurgePolicy.ComputeHash(db.Options), default);

        Assert.NotNull(approvazione);
        Assert.Equal("secondo", approvazione!.ApprovedBy);
    }

    /// <summary>
    /// Ogni run registra la policy sotto cui e' girato: senza, dall'id di un
    /// dry-run non si risalirebbe a cosa e' stato esaminato, e il comando di
    /// approvazione non potrebbe verificare la corrispondenza.
    /// </summary>
    [Fact]
    public async Task Il_run_registra_la_policy_sotto_cui_e_girato()
    {
        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

        var salvata = await db.Sql.ScalarAsync<string>(
            "SELECT PolicyHash FROM Purge.PurgeRun WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

        Assert.Equal(PurgePolicy.ComputeHash(db.Options), salvata);
    }

    /// <summary>
    /// L'approvazione vive nel database, non in configurazione: e' cio' che
    /// impedisce a un appsettings copiato di autorizzare qualcosa. Ripulendo
    /// la tabella, il gate torna a rifiutare.
    /// </summary>
    [Fact]
    public async Task L_approvazione_vive_nel_database()
    {
        await ApprovaPolicyCorrenteAsync();
        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));

        await db.Sql.ExecuteAsync("DELETE FROM Purge.PolicyApproval;", default);

        Assert.False(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
    }

    /// <summary>
    /// D-19: alzare il tetto per aggregato invalida l'approvazione.
    ///
    /// E' il caso che il gate esiste per intercettare, ed e' la prova
    /// end-to-end di D-19: si approva una policy in cui un aggregato da 80.000
    /// righe resta escluso, poi si alza il tetto a 100.000. Senza il tetto
    /// nell'impronta, quell'aggregato diventerebbe cancellabile con
    /// un'approvazione che nessuno ha dato per lui.
    /// </summary>
    [Fact]
    public async Task Alzare_il_tetto_da_30000_a_100000_invalida_l_approvazione()
    {
        var opzioni = db.Options;
        opzioni.MaxAggregateWeight = 30_000;

        await ApprovaPolicyCorrenteAsync();
        Assert.True(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));

        opzioni.MaxAggregateWeight = 100_000;

        Assert.False(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
    }

    /// <summary>Vale anche disattivandolo: e' comunque un perimetro diverso.</summary>
    [Fact]
    public async Task Disattivare_il_tetto_per_aggregato_invalida_l_approvazione()
    {
        var opzioni = db.Options;
        opzioni.MaxAggregateWeight = 30_000;

        await ApprovaPolicyCorrenteAsync();
        opzioni.MaxAggregateWeight = 0;

        Assert.False(await Gate.IsAllowedAsync(PurgeExecutionMode.Delete, default));
    }
}
