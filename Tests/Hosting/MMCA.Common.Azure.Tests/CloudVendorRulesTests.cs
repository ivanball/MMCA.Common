using AwesomeAssertions;
using Azure.Storage.Blobs;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Azure.Tests;

/// <summary>
/// Pins that the type-level cloud-vendor rule actually matches Azure SDK types, through the <c>Azure</c> namespace
/// root. The entries were once written with a trailing dot,
/// which NetArchTest never matches, so the rule passed vacuously; an SDK assembly names its own types
/// and must fail it.
/// </summary>
public sealed class CloudVendorRulesTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoAllowance =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The <c>Azure</c> root: the Blob SDK names <c>Azure.Core</c> types.
    /// </summary>
    [Fact]
    public void AnAssemblyNamingAnAzureSdkType_FailsTheTypeRule()
    {
        var act = () => ArchitectureRules.CloudNeutralAssembliesNameNoVendorType([typeof(BlobServiceClient).Assembly], NoAllowance);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("must stay cloud-neutral");
    }
}
