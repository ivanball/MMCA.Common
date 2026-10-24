using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using MMCA.Common.Aws.Storage;

namespace MMCA.Common.Aws.Tests.Storage;

/// <summary>
/// The S3 client configuration built from <see cref="S3FileStorageSettings"/>: endpoint versus
/// region, addressing style, signing region and the checksum posture.
/// </summary>
public sealed class S3StorageClientFactoryTests
{
    [Fact]
    public void Region_WithoutServiceUrl_SelectsTheAwsRegionEndpoint()
    {
        var config = S3StorageClientFactory.BuildConfig(new S3FileStorageSettings { Bucket = "b", Region = "ap-southeast-2" });

        config.RegionEndpoint.Should().Be(RegionEndpoint.APSoutheast2);
        config.ServiceURL.Should().BeNull();
        config.ForcePathStyle.Should().BeFalse();
    }

    [Fact]
    public void ServiceUrl_SelectsTheEndpoint_AndTheRegionBecomesTheSigningRegion()
    {
        var config = S3StorageClientFactory.BuildConfig(new S3FileStorageSettings
        {
            Bucket = "b",
            Region = "auto",
            ServiceUrl = new Uri("https://acct.r2.cloudflarestorage.com"),
            ForcePathStyle = true,
        });

        config.ServiceURL.Should().StartWith("https://acct.r2.cloudflarestorage.com");
        config.AuthenticationRegion.Should().Be("auto");
        config.ForcePathStyle.Should().BeTrue();
    }

    [Fact]
    public void ChecksumPosture_DefaultsToWhenRequired_AndHonorsAnOverride()
    {
        var defaults = S3StorageClientFactory.BuildConfig(new S3FileStorageSettings { Bucket = "b", Region = "us-east-1" });
        var overridden = S3StorageClientFactory.BuildConfig(new S3FileStorageSettings
        {
            Bucket = "b",
            Region = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_SUPPORTED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_SUPPORTED,
        });

        defaults.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_REQUIRED);
        defaults.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_REQUIRED);
        overridden.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_SUPPORTED);
        overridden.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_SUPPORTED);
    }

    [Fact]
    public void Create_WithAKeyPair_BuildsAClient()
    {
        using var client = (IDisposable)S3StorageClientFactory.Create(new S3FileStorageSettings
        {
            Bucket = "b",
            ServiceUrl = new Uri("http://localhost:9000"),
            AccessKeyId = "minio",
            SecretAccessKey = "minio123",
        });

        client.Should().NotBeNull();
    }

    [Fact]
    public void BuildConfig_RejectsNullSettings()
    {
        var act = () => S3StorageClientFactory.BuildConfig(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
