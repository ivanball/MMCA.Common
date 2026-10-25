using AwesomeAssertions;
using MMCA.Common.Gcp.Storage;

namespace MMCA.Common.Gcp.Tests.Storage;

/// <summary>
/// The client the registration builds: Google Cloud's endpoint with Application Default Credentials
/// by default, the JSON API under an endpoint override, credentials dropped only for a plain-HTTP
/// (emulator) endpoint, and the quota project from <c>GcsProjectId</c>.
/// </summary>
public sealed class GcsStorageClientFactoryTests
{
    [Fact]
    public void BuildBuilder_WithoutAnOverride_KeepsTheGoogleEndpointAndCredentials()
    {
        var builder = GcsStorageClientFactory.BuildBuilder(new GcsFileStorageSettings { GcsBucket = "avatars" });

        builder.BaseUri.Should().BeNull();
        builder.UnauthenticatedAccess.Should().BeFalse();
        builder.QuotaProject.Should().BeNull();
    }

    [Theory]
    [InlineData("http://localhost:4443")]
    [InlineData("http://localhost:4443/")]
    public void BuildBuilder_WithAPlainHttpOverride_TargetsItsJsonApi_WithoutCredentials(string serviceUrl)
    {
        var builder = GcsStorageClientFactory.BuildBuilder(new GcsFileStorageSettings { GcsBucket = "avatars", GcsServiceUrl = new Uri(serviceUrl) });

        builder.BaseUri.Should().Be("http://localhost:4443/storage/v1/");
        builder.UnauthenticatedAccess.Should().BeTrue("a plain-HTTP endpoint is an emulator, and a bearer token must not travel in clear");
    }

    [Fact]
    public void BuildBuilder_WithAnHttpsOverride_KeepsCredentials()
    {
        var builder = GcsStorageClientFactory.BuildBuilder(new GcsFileStorageSettings
        {
            GcsBucket = "avatars",
            GcsServiceUrl = new Uri("https://storage-acme.p.googleapis.com"),
        });

        builder.BaseUri.Should().Be("https://storage-acme.p.googleapis.com/storage/v1/");
        builder.UnauthenticatedAccess.Should().BeFalse();
    }

    [Theory]
    [InlineData("acme-prod", "acme-prod")]
    [InlineData("   ", null)]
    public void BuildBuilder_MapsTheProjectIdToTheQuotaProject(string projectId, string? expected)
    {
        var builder = GcsStorageClientFactory.BuildBuilder(new GcsFileStorageSettings { GcsBucket = "avatars", GcsProjectId = projectId });

        builder.QuotaProject.Should().Be(expected);
    }

    [Fact]
    public void Create_WithAnEmulatorEndpoint_BuildsAClientOnIt()
    {
        using var client = GcsStorageClientFactory.Create(new GcsFileStorageSettings
        {
            GcsBucket = "avatars",
            GcsServiceUrl = new Uri("http://localhost:4443"),
        });

        client.Service.BaseUri.Should().Be("http://localhost:4443/storage/v1/");
    }
}
