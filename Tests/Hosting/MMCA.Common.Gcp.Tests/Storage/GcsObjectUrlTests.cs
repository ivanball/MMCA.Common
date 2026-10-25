using AwesomeAssertions;
using MMCA.Common.Gcp.Storage;

namespace MMCA.Common.Gcp.Tests.Storage;

/// <summary>
/// The URL an upload returns: the public base when one is configured, else the endpoint (the
/// override or Google Cloud's own) plus the bucket, with every segment of the object name escaped
/// and its separators kept, the same composition rules as the S3 object URL.
/// </summary>
public sealed class GcsObjectUrlTests
{
    [Fact]
    public void Compose_DefaultsToTheStandardCloudStorageForm()
    {
        var url = GcsObjectUrl.Compose(new GcsFileStorageSettings { GcsBucket = "avatars" }, "users/42/a.png");

        url.Should().Be(new Uri("https://storage.googleapis.com/avatars/users/42/a.png"));
    }

    [Fact]
    public void Compose_EscapesEachSegment_AndKeepsTheSeparators()
    {
        var url = GcsObjectUrl.Compose(new GcsFileStorageSettings { GcsBucket = "documents" }, "users/42/q1 report #2 é.pdf");

        url.AbsoluteUri.Should().Be("https://storage.googleapis.com/documents/users/42/q1%20report%20%232%20%C3%A9.pdf");
    }

    [Theory]
    [InlineData("https://cdn.example.com")]
    [InlineData("https://cdn.example.com/")]
    public void Compose_UsesThePublicBaseUrl_WithoutRepeatingTheBucket(string publicBase)
    {
        var settings = new GcsFileStorageSettings
        {
            GcsBucket = "avatars",
            GcsPublicBaseUrl = new Uri(publicBase),
            GcsServiceUrl = new Uri("http://localhost:4443"),
        };

        GcsObjectUrl.Compose(settings, "users/42/a.png").Should().Be(new Uri("https://cdn.example.com/users/42/a.png"));
    }

    [Fact]
    public void Compose_UsesTheServiceUrl_WhenNoPublicBaseIsSet()
    {
        var settings = new GcsFileStorageSettings { GcsBucket = "avatars", GcsServiceUrl = new Uri("http://localhost:4443/") };

        GcsObjectUrl.Compose(settings, "a b.png").Should().Be(new Uri("http://localhost:4443/avatars/a%20b.png"));
    }
}
