using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSM.PaymentOrder.Purge.Domain;
using OSM.PaymentOrder.Purge.Engine;
using OSM.PaymentOrder.Purge.Engine.BatchExecution;
using OSM.PaymentOrder.Purge.Observability;

namespace OSM.PaymentOrder.Purge.Tests;

public sealed class BatchExecutionCoordinatorContractTests
{
    // Contract-level smoke test: verifies the new abstractions are constructible
    // and keep the coordinator independent from PurgeRunStore/SliceExecutor.
    [Fact]
    public void Coordinator_depends_on_abstractions()
    {
        var ctor = typeof(BatchExecutionCoordinator)
            .GetConstructors()
            .Single();

        var parameters = ctor.GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IBatchWorkProvider), parameters);
        Assert.Contains(typeof(IBatchExecutor), parameters);
        Assert.DoesNotContain(typeof(PurgeRunStore), parameters);
        Assert.DoesNotContain(typeof(SliceExecutor), parameters);
    }
}
