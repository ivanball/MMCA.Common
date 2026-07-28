using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Cross-assembly guard for <see cref="ObservabilityConventionTestsBase"/>. The base ships inside
/// the MMCA.Common.Testing.Architecture package but must read the embedded IaC resources of the
/// SUBCLASS's assembly. Resolving against the base's own assembly instead is a silent break: the
/// framework's own CI would stay green and the failure would only appear in the first consumer that
/// adopted it. This subclass lives in a different assembly from the base and points at fixture
/// resources embedded here, so inheriting the three [Fact]s exercises the whole discovery and
/// pairing path across the assembly boundary.
/// </summary>
public sealed class ObservabilityConventionTestsBaseTests : ObservabilityConventionTestsBase
{
    protected override string BicepResource => "fixtures.observability-main.bicep";

    protected override string RunbookResource => "fixtures.observability-OPERATIONS.md";

    /// <summary>
    /// The regression this file exists for: the default must resolve to the DERIVED type's
    /// assembly, not the assembly the base was compiled into.
    /// </summary>
    [Fact]
    public void ResourceAssembly_DefaultsToTheDerivedTypesAssembly()
    {
        ResourceAssembly.Should().BeSameAs(typeof(ObservabilityConventionTestsBaseTests).Assembly);
        ResourceAssembly.Should().NotBeSameAs(typeof(ObservabilityConventionTestsBase).Assembly);
    }
}
