using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// DDD aggregate-root factory rules for the framework's own aggregates, driven by the shared
/// <see cref="AggregateConventionTestsBase"/>.
/// </summary>
public sealed class AggregateConventionTests : AggregateConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
