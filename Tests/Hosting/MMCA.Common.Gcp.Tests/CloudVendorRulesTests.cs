using System.Reflection;
using AwesomeAssertions;
using Google.Cloud.Storage.V1;
using MMCA.Common.Gcp.Storage;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Gcp.Tests;

/// <summary>
/// The cloud-vendor rules reject Google Cloud: an assembly declared cloud-neutral that names a Google
/// SDK type, references a Google SDK assembly, or references the Google Cloud adapter fails them.
/// The positive half (the framework's own core assemblies still pass) is
/// <c>CloudVendorIsolationTests</c> in the architecture tier.
/// <para>
/// The fixtures are the Google Cloud Storage client assembly, whose types name only <c>Google.*</c>
/// vendor types outside their own assembly, so nothing but the Google entries can make the rules fail,
/// and this test assembly, which references <c>MMCA.Common.Gcp</c>.
/// </para>
/// </summary>
public sealed class CloudVendorRulesTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoAllowance =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    private static Assembly GoogleStorageSdk => typeof(StorageClient).Assembly;

    [Fact]
    public void AnAssemblyNamingAGoogleSdkType_FailsTheTypeRule()
    {
        var act = () => ArchitectureRules.CloudNeutralAssembliesNameNoVendorType([GoogleStorageSdk], NoAllowance);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void AnAssemblyReferencingAGoogleSdkAssembly_FailsTheAssemblyRule()
    {
        var act = () => ArchitectureRules.CloudNeutralAssembliesReferenceNoVendorAssembly([GoogleStorageSdk], NoAllowance);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("Google.Apis");
    }

    [Fact]
    public void AnAssemblyReferencingTheGoogleCloudAdapter_FailsTheAssemblyRule()
    {
        // Touches an adapter type so the compiler keeps the reference this assertion is about.
        _ = typeof(GcsFileStorageService);

        var act = () => ArchitectureRules.CloudNeutralAssembliesReferenceNoVendorAssembly([typeof(CloudVendorRulesTests).Assembly], NoAllowance);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("MMCA.Common.Gcp");
    }
}
