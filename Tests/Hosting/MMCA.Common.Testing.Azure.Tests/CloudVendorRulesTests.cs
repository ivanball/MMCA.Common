using AwesomeAssertions;
using MMCA.Common.Testing.Architecture;
using MMCA.Common.Testing.Azure.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Azure.Tests;

/// <summary>
/// Pins that the type-level cloud-vendor rule treats the MMCA.Common.Testing.Azure namespace as an
/// adapter. The assembly rule cannot pin it: it also catches any referenced name ending in ".Azure", so
/// that entry could be dropped from its list unnoticed. The type rule matches namespaces only, so this
/// class, whose single vendor dependency is the adapter type it names, must be listed as a violation.
/// The sibling adapter test projects pin their own entries the same way.
/// </summary>
public sealed class CloudVendorRulesTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoAllowance =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    [Fact]
    public void ATypeNamingTheAzureTestingAdapter_FailsTheTypeRule()
    {
        // The adapter type is this class's only vendor dependency, so it is flagged for that alone.
        _ = typeof(ServiceBusEmulatorFixtureBase);

        var act = () => ArchitectureRules.CloudNeutralAssembliesNameNoVendorType([typeof(CloudVendorRulesTests).Assembly], NoAllowance);

        act.Should().Throw<Exception>().Which.Message.Should().Contain($"- {typeof(CloudVendorRulesTests).FullName}");
    }
}
