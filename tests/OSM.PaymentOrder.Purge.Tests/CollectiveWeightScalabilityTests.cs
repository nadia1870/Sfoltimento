using OSM.PaymentOrder.Purge.Domain;

namespace OSM.PaymentOrder.Purge.Tests;

[Collection("PurgeDatabase")]
[Trait("Category", "Integration")]
public sealed class CollectiveWeightScalabilityTests(PurgeDatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private SeedBuilder Seed => new(db.Sql);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(5, 0)]
    [InlineData(10, 0)]
    [InlineData(25, 0)]
    [InlineData(5, 3)]
    [InlineData(10, 7)]
    public async Task EstimatedRows_scales_with_all_group_order_rows(
        int components,
        int rejectedRows)
    {
        var collectiveId = await Seed.AddCollectiveAsync(
            components: components,
            rejectedRows: rejectedRows);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Collective,
            db.Options,
            DateTimeOffset.Now,
            default);

        var run = await db.Store.LoadAsync(runId, default);
        var strategy = db.Strategies.Resolve(run.Strategy);

        var selected = await strategy.SelectAsync(run, default);

        // SelectAsync returns the number of component orders selected,
        // not the number of CollectiveOrder records.
        Assert.Equal(components, selected);

        var result = await db.Sql.QueryAsync(
            """
            SELECT OrderCount, EstimatedRows
            FROM Purge.RunCandidateCollective
            WHERE RunId = @RunId
              AND CollectiveOrderId = @CollectiveOrderId;
            """,
            r => (
                OrderCount: r.GetInt32(0),
                EstimatedRows: r.GetInt32(1)),
            default,
            new("@RunId", run.RunId),
            new("@CollectiveOrderId", collectiveId));

        var row = Assert.Single(result);

        var groupOrderCount = components + rejectedRows;
        var expectedEstimatedRows = groupOrderCount * 4 + 5;

        Assert.Equal(groupOrderCount, row.OrderCount);
        Assert.Equal(expectedEstimatedRows, row.EstimatedRows);
    }

    [Fact]
    public async Task EstimatedRows_is_not_used_as_collective_batch_weight()
    {
        var collectiveId = await Seed.AddCollectiveAsync(components: 20);

        var runId = await db.Store.CreateAsync(
            RetentionStrategy.Collective,
            db.Options,
            DateTimeOffset.Now,
            default);

        var run = await db.Store.LoadAsync(runId, default);
        var strategy = db.Strategies.Resolve(run.Strategy);

        // SelectAsync returns the number of component orders.
        Assert.Equal(20, await strategy.SelectAsync(run, default));

        await strategy.ExpandAsync(run, default);

        var slices = await db.Planner.PlanAsync(run, default);

        Assert.Equal(1, slices);

        var collective = await db.Sql.QueryAsync(
            """
            SELECT
                OrderCount,
                EstimatedRows,
                BatchNo
            FROM Purge.RunCandidateCollective
            WHERE RunId = @RunId
              AND CollectiveOrderId = @CollectiveOrderId;
            """,
            r => (
                OrderCount: r.GetInt32(0),
                EstimatedRows: r.GetInt32(1),
                BatchNo: r.GetInt32(2)),
            default,
            new("@RunId", run.RunId),
            new("@CollectiveOrderId", collectiveId));

        var row = Assert.Single(collective);

        Assert.Equal(20, row.OrderCount);
        Assert.Equal(85, row.EstimatedRows);

        var planned = await db.Sql.QueryAsync(
            """
            SELECT
                COUNT_BIG(*) AS OrderCount,
                SUM(RowWeight) AS RowWeight
            FROM Purge.RunCandidateOrder
            WHERE RunId = @RunId
              AND CollectiveOrderId = @CollectiveOrderId
              AND BatchNo = @BatchNo;
            """,
            r => (
                OrderCount: r.GetInt64(0),
                RowWeight: r.GetInt32(1)),
            default,
            new("@RunId", run.RunId),
            new("@CollectiveOrderId", collectiveId),
            new("@BatchNo", row.BatchNo));

        var plannedRow = Assert.Single(planned);

        Assert.Equal(20, plannedRow.OrderCount);
        Assert.Equal(60, plannedRow.RowWeight);

        // With MaxRowsPerBatch = 50, the 20 component orders (60 RowWeight)
        // cannot be packed as a normal batch. The planner therefore emits
        // the aggregate as one oversized slice, as confirmed by its warning.
        Assert.True(plannedRow.RowWeight > db.Options.MaxRowsPerBatch);
    }
}
