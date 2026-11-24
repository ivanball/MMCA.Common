using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// The shipped S3 key ring, registered through <c>AddAwsDataProtection</c> exactly as a host would,
/// against LocalStack's S3: two independent hosts on one object share one key ring, so a payload
/// protected by either is readable by the other. That is the whole point of the call, the scaled-out
/// host whose replicas must read each other's cookies. It also settles what the unit tier can only
/// assume: that the endpoint honours the conditional writes the repository sends.
/// </summary>
public sealed class AwsDataProtectionLocalStackTests(LocalStackFixture localStack)
{
    [Fact]
    public async Task TwoHostsOnOneObject_ShareOneKeyRing()
    {
        var bucket = $"mmca-keys-{Guid.NewGuid():N}";
        using (var s3 = localStack.CreateS3Client())
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, TestContext.Current.CancellationToken);
        }

        var objectUri = $"s3://{bucket}/dataprotection/keys.xml";

        using var replicaA = BuildReplica(objectUri);
        using var replicaB = BuildReplica(objectUri);

        // A protects first, which creates the key and writes it to the object; B's key ring is loaded
        // lazily on its first use, so B reads the key A wrote rather than minting its own.
        var protectorA = replicaA.Services.GetDataProtector("cookies");
        var protectorB = replicaB.Services.GetDataProtector("cookies");

        var fromA = protectorA.Protect("minted-by-a");
        protectorB.Unprotect(fromA).Should().Be("minted-by-a");

        var fromB = protectorB.Protect("minted-by-b");
        protectorA.Unprotect(fromB).Should().Be("minted-by-b");
    }

    private IHost BuildReplica(string objectUri)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DataProtection:S3ObjectUri"] = objectUri,
            ["DataProtection:ApplicationName"] = "mmca-localstack-tests",
        });

        // The host's own client, pointed at LocalStack; the package resolves it from the container.
        builder.Services.AddSingleton<IAmazonS3>(localStack.CreateS3Client());
        builder.AddAwsDataProtection();

        return builder.Build();
    }
}
