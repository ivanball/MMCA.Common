using Amazon.SimpleSystemsManagement;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// The shipped Systems Manager key ring, registered through <c>AddAwsDataProtection</c> exactly as a
/// host would, against LocalStack's Parameter Store: two independent hosts on one parameter prefix
/// share one key ring, so a payload protected by either is readable by the other. That is the whole
/// point of the call, the scaled-out host whose replicas must read each other's cookies.
/// </summary>
public sealed class AwsDataProtectionLocalStackTests(LocalStackFixture localStack)
{
    [Fact]
    public void TwoHostsOnOnePrefix_ShareOneKeyRing()
    {
        var prefix = $"/mmca-{Guid.NewGuid():N}/dataprotection";

        using var replicaA = BuildReplica(prefix);
        using var replicaB = BuildReplica(prefix);

        // A protects first, which creates the key and writes it to Parameter Store; B's key ring is
        // loaded lazily on its first use, so B reads the key A wrote rather than minting its own.
        var protectorA = replicaA.Services.GetDataProtector("cookies");
        var protectorB = replicaB.Services.GetDataProtector("cookies");

        var fromA = protectorA.Protect("minted-by-a");
        protectorB.Unprotect(fromA).Should().Be("minted-by-a");

        var fromB = protectorB.Protect("minted-by-b");
        protectorA.Unprotect(fromB).Should().Be("minted-by-b");
    }

    private IHost BuildReplica(string prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DataProtection:SsmParameterPrefix"] = prefix,
            ["DataProtection:ApplicationName"] = "mmca-localstack-tests",
        });

        // The host's own client, pointed at LocalStack; the package resolves it from the container.
        builder.Services.AddSingleton<IAmazonSimpleSystemsManagement>(localStack.CreateSsmClient());
        builder.AddAwsDataProtection();

        return builder.Build();
    }
}
