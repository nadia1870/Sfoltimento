using OSM.PaymentOrder.Purge.Engine;
using System.Reflection;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using Xunit;

namespace OSM.PaymentOrder.Purge.Tests;

public sealed class BatchExecutionCoordinatorContractTests
{
    [Fact]
    public void Coordinator_depends_on_abstractions()
    {
        var ctor = typeof(BatchExecutionCoordinator).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Contains(typeof(IBatchWorkProvider), parameterTypes);
        Assert.Contains(typeof(IBatchExecutor), parameterTypes);
        Assert.DoesNotContain(typeof(PurgeRunStore), parameterTypes);
        Assert.DoesNotContain(typeof(SliceExecutor), parameterTypes);
    }
}
