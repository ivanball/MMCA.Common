using AwesomeAssertions;
using MMCA.Common.Aws.Storage;

namespace MMCA.Common.Aws.Tests.Storage;

/// <summary>
/// The URL an S3 upload returns: a configured public base wins, else the endpoint, bucket and
/// addressing style compose it, and the key is percent-encoded segment by segment.
/// </summary>
public sealed class S3ObjectUrlTests
{
    [Theory]
    [InlineData("https://cdn.example.com", "https://cdn.example.com/avatars/42.png")]
    [InlineData("https://cdn.example.com/", "https://cdn.example.com/avatars/42.png")]
    [InlineData("https://cdn.example.com/media/", "https://cdn.example.com/media/avatars/42.png")]
    public void PublicBaseUrl_WinsAndDoesNotRepeatTheBucket(string publicBase, string expected)
    {
        var settings = new S3FileStorageSettings
        {
            Bucket = "bucket",
            PublicBaseUrl = new Uri(publicBase),
            ServiceUrl = new Uri("https://acct.r2.cloudflarestorage.com"),
            ForcePathStyle = true,
        };

        S3ObjectUrl.Compose(settings, "auto", "avatars/42.png").Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("http://localhost:4566", "http://localhost:4566/bucket/avatars/42.png")]
    [InlineData("http://minio:9000/", "http://minio:9000/bucket/avatars/42.png")]
    public void ServiceUrl_PathStyle_PutsTheBucketInThePath(string serviceUrl, string expected)
    {
        var settings = new S3FileStorageSettings { Bucket = "bucket", ServiceUrl = new Uri(serviceUrl), ForcePathStyle = true };

        S3ObjectUrl.Compose(settings, region: null, "avatars/42.png").Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("https://acct.r2.cloudflarestorage.com", "https://bucket.acct.r2.cloudflarestorage.com/avatars/42.png")]
    [InlineData("http://minio:9000", "http://bucket.minio:9000/avatars/42.png")]
    public void ServiceUrl_VirtualHosted_PutsTheBucketInTheHost(string serviceUrl, string expected)
    {
        var settings = new S3FileStorageSettings { Bucket = "bucket", ServiceUrl = new Uri(serviceUrl) };

        S3ObjectUrl.Compose(settings, "auto", "avatars/42.png").Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData(false, "https://bucket.s3.eu-west-1.amazonaws.com/avatars/42.png")]
    [InlineData(true, "https://s3.eu-west-1.amazonaws.com/bucket/avatars/42.png")]
    public void AwsS3_ComposesTheRegionalEndpoint(bool pathStyle, string expected)
    {
        var settings = new S3FileStorageSettings { Bucket = "bucket", ForcePathStyle = pathStyle };

        S3ObjectUrl.Compose(settings, "eu-west-1", "avatars/42.png").Should().Be(new Uri(expected));
    }

    [Fact]
    public void Key_IsEscapedPerSegment_KeepingItsSlashes()
    {
        var settings = new S3FileStorageSettings { Bucket = "bucket", ForcePathStyle = true };

        S3ObjectUrl.Compose(settings, "us-east-1", "docs/q1 report #2.pdf").AbsoluteUri
            .Should().Be("https://s3.us-east-1.amazonaws.com/bucket/docs/q1%20report%20%232.pdf");
    }

    [Fact]
    public void RelativeUrls_CountAsUnset()
    {
        // An empty-string URL binds to a RELATIVE Uri; only an absolute one selects a branch.
        var settings = new S3FileStorageSettings
        {
            Bucket = "bucket",
            PublicBaseUrl = new Uri(string.Empty, UriKind.Relative),
            ServiceUrl = new Uri(string.Empty, UriKind.Relative),
        };

        S3ObjectUrl.Compose(settings, "us-east-2", "a.png").Should().Be(new Uri("https://bucket.s3.us-east-2.amazonaws.com/a.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AwsS3_WithoutARegion_Throws(string? region)
    {
        var act = () => S3ObjectUrl.Compose(new S3FileStorageSettings { Bucket = "bucket" }, region, "a.png");

        act.Should().Throw<InvalidOperationException>().WithMessage("*FileStorage:Region*");
    }
}
