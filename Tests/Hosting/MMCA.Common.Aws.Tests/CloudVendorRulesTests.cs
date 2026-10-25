using System.Reflection;
using Amazon.S3;
using AwesomeAssertions;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Aws.Tests;

/// <summary>
/// Pins that the type-level cloud-vendor rule actually matches AWS SDK types. The namespace entries
/// were once written with a trailing dot, which NetArchTest never matches, so the rule passed
/// vacuously for every AWS type; an SDK assembly names its own types and must fail it.
/// </summary>
public sealed class CloudVendorRulesTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoAllowance =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    private static Assembly AmazonS3Sdk => typeof(AmazonS3Client).Assembly;

    [Fact]
    public void AnAssemblyNamingAnAmazonSdkType_FailsTheTypeRule()
    {
        var act = () => ArchitectureRules.CloudNeutralAssembliesNameNoVendorType([AmazonS3Sdk], NoAllowance);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("must stay cloud-neutral");
    }
}
