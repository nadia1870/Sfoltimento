using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Tests;

/// <summary>
/// Regression tests for the retention window frozen on PurgeRun creation.
///
/// The run stores the calculated cutoffs. Subsequent selection must use those
/// persisted values rather than deriving a new cutoff from the time at which
/// the run is resumed.
/// </summary>
[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class ExecutionWindowRegressionTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    [Fact]
    public async Task Create_and_load_preserves_the_frozen_retention_cutoff()
    {
        var reference = new DateTimeOffset(
            2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var expectedCutoff = db.Options.ComputeRetentionCutoff(reference);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated,
            db.Options,
            reference,
            default);

        var run = await db.Store.LoadAsync(runId, default);

        Assert.Equal(expectedCutoff, run.RetentionCutoff);
        Assert.Equal(db.Options.AnchorMode, run.AnchorMode);
    }

    [Fact]
    public async Task Resumed_selection_uses_the_frozen_cutoff_not_a_newer_one()
    {
        var originalReference = new DateTimeOffset(
            2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var laterReference = originalReference.AddYears(1);

        var originalCutoff =
            db.Options.ComputeRetentionCutoff(originalReference);
        var laterCutoff =
            db.Options.ComputeRetentionCutoff(laterReference);

        // This order is newer than the cutoff frozen at run creation, but
        // would become eligible if selection recalculated the cutoff one year
        // later.
        var executionDate = originalCutoff.AddMonths(6);
        Assert.True(executionDate >= originalCutoff);
        Assert.True(executionDate < laterCutoff);

        await Seed.AddOrderAsync(
            executionDate: executionDate,
            creationDate: executionDate,
            revisions: 1);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Terminated,
            db.Options,
            originalReference,
            default);

        // Simulate a resume: the run is reloaded from persistence after time
        // has moved on. No later reference date is passed to SelectAsync.
        var resumedRun = await db.Store.LoadAsync(runId, default);
        var strategy = db.Strategies.Resolve(RetentionStrategy.Terminated);

        Assert.Equal(originalCutoff, resumedRun.RetentionCutoff);

        var selected = await strategy.SelectAsync(resumedRun, default);

        Assert.Equal(0, selected);

        var candidates = await db.Sql.ScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId;
            """,
            default,
            OSM.PaymentOrder.Purge.Data.SqlParam.Of("@RunId", runId));

        Assert.Equal(0, candidates);
    }
}
