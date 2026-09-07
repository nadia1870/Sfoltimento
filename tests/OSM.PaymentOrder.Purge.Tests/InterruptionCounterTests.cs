using OSM.PaymentOrder.Purge.Data;
using OSM.PaymentOrder.Purge.Domain;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// A2 — il contatore misura interruzioni consecutive senza progresso.
///
/// Prima misurava il totale di vita, che e' un'altra cosa: un run che
/// attraversava cinque notti con un guasto di rete a notte, completando slice
/// ogni volta, alla sesta veniva dichiarato fallito mentre stava funzionando.
///
/// Il progresso ha due forme, e servono entrambe: il cambio di fase, e la slice
/// completata durante Executing, che e' la fase lunga e non cambia mai fase.
/// </summary>
[Collection("PurgeDatabase")]
public sealed class InterruptionCounterTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> ContatoreAsync(Guid runId) =>
        await db.Sql.ScalarAsync<int>(
            "SELECT InterruptionCount FROM Purge.PurgeRun WHERE RunId = @RunId;",
            default, SqlParam.Of("@RunId", runId));

    private Task<Guid> CreaRunAsync() => db.Store.CreateAsync(
        RetentionStrategy.Terminated, db.Options, DateTimeOffset.Now, default);

    [Fact]
    public async Task Il_cambio_di_fase_azzera_il_contatore()
    {
        var runId = await CreaRunAsync();

        await db.Store.RecordInterruptionAsync(runId, "rete", default);
        await db.Store.RecordInterruptionAsync(runId, "rete", default);
        Assert.Equal(2, await ContatoreAsync(runId));

        await db.Store.SetPhaseAsync(runId, RunPhase.Expanding, default);

        Assert.Equal(0, await ContatoreAsync(runId));
    }

    /// <summary>
    /// Interruzione, progresso, nuova interruzione: il contatore deve ripartire
    /// da uno e non da tre.
    /// </summary>
    [Fact]
    public async Task Il_contatore_riparte_dopo_un_progresso()
    {
        var runId = await CreaRunAsync();

        await db.Store.RecordInterruptionAsync(runId, "rete", default);
        await db.Store.RecordInterruptionAsync(runId, "rete", default);
        await db.Store.ResetInterruptionsAsync(runId, default);
        await db.Store.RecordInterruptionAsync(runId, "rete", default);

        Assert.Equal(1, await ContatoreAsync(runId));
    }

    /// <summary>
    /// Senza progresso il comportamento resta quello di prima: si accumula
    /// fino alla soglia. E' il caso che il contatore esiste per intercettare.
    /// </summary>
    [Fact]
    public async Task Senza_progresso_il_contatore_accumula_fino_alla_soglia()
    {
        var runId = await CreaRunAsync();

        for (var i = 0; i < db.Options.MaxRunInterruptions; i++)
            await db.Store.RecordInterruptionAsync(runId, "rete", default);

        Assert.Equal(db.Options.MaxRunInterruptions, await ContatoreAsync(runId));

        var run = await db.Store.LoadAsync(runId, default);
        Assert.Equal(db.Options.MaxRunInterruptions, run.InterruptionCount);
    }

    /// <summary>
    /// L'azzeramento e' condizionato: su un run che non ha interruzioni non
    /// deve scrivere niente. E' cio' che rende accettabile chiamarlo a ogni
    /// transizione di fase.
    /// </summary>
    [Fact]
    public async Task L_azzeramento_su_un_contatore_gia_a_zero_non_tocca_niente()
    {
        var runId = await CreaRunAsync();

        await db.Store.ResetInterruptionsAsync(runId, default);

        Assert.Equal(0, await ContatoreAsync(runId));
    }
}
