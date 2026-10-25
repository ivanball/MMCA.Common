using AwesomeAssertions;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Gcp.Storage;
using Moq;

namespace MMCA.Common.Gcp.Tests;

/// <summary>
/// The Cloud Storage registration: it swaps the inert core default only when the shared
/// <c>FileStorage</c> section names a <c>GcsBucket</c> (never on the S3 or Azure keys), binds every
/// <c>Gcs*</c> key, and reuses a client the host registered first.
/// </summary>
public sealed class GcpServiceCollectionExtensionsTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Type? ImplementationOf<TService>(IServiceCollection services) =>
        services.LastOrDefault(d => d.ServiceType == typeof(TService))?.ImplementationType;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddGoogleCloudStorage_WithoutAGcsBucket_IsANoOp_EvenWhenTheS3AndAzureKeysAreSet(string? gcsBucket)
    {
        var services = new ServiceCollection();

        services.AddGoogleCloudStorage(ConfigurationWith(new()
        {
            ["FileStorage:GcsBucket"] = gcsBucket,
            ["FileStorage:Bucket"] = "s3-bucket",
            ["FileStorage:ContainerName"] = "azure-container",
            ["FileStorage:GcsServiceUrl"] = "http://localhost:4443",
        }));

        ImplementationOf<IFileStorageService>(services).Should().BeNull();
        services.Should().NotContain(d => d.ServiceType == typeof(StorageClient));
    }

    [Fact]
    public void AddGoogleCloudStorage_WithAGcsBucket_RegistersTheCloudStorageService()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddGoogleCloudStorage(ConfigurationWith(new()
        {
            ["FileStorage:GcsBucket"] = "avatars",

            // A plain-HTTP endpoint builds an unauthenticated client, so resolving it needs no
            // Application Default Credentials on the test machine.
            ["FileStorage:GcsServiceUrl"] = "http://localhost:4443",
        }));

        ImplementationOf<IFileStorageService>(services).Should().Be<GcsFileStorageService>();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<StorageClient>().Service.BaseUri.Should().Be("http://localhost:4443/storage/v1/");
        provider.GetRequiredService<IFileStorageService>().Should().BeOfType<GcsFileStorageService>()
            .Which.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void AddGoogleCloudStorage_ReusesAStorageClientTheHostRegisteredFirst()
    {
        var hostClient = new Mock<StorageClient>().Object;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(hostClient);

        services.AddGoogleCloudStorage(ConfigurationWith(new() { ["FileStorage:GcsBucket"] = "avatars" }));

        services.Where(d => d.ServiceType == typeof(StorageClient)).Should().ContainSingle();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<StorageClient>().Should().BeSameAs(hostClient);
        provider.GetRequiredService<IFileStorageService>().Should().BeOfType<GcsFileStorageService>();
    }

    [Fact]
    public void AddGoogleCloudStorage_BindsEverySetting()
    {
        var services = new ServiceCollection();

        services.AddGoogleCloudStorage(ConfigurationWith(new()
        {
            ["FileStorage:GcsBucket"] = "documents",
            ["FileStorage:GcsPublicBaseUrl"] = "https://cdn.example.com/",
            ["FileStorage:GcsServiceUrl"] = "http://localhost:4443",
            ["FileStorage:GcsProjectId"] = "acme-prod",
        }));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<GcsFileStorageSettings>>().Value;

        settings.GcsBucket.Should().Be("documents");
        settings.GcsPublicBaseUrl.Should().Be(new Uri("https://cdn.example.com/"));
        settings.GcsServiceUrl.Should().Be(new Uri("http://localhost:4443"));
        settings.GcsProjectId.Should().Be("acme-prod");
    }

    [Fact]
    public void AddGoogleCloudStorage_RejectsNullConfiguration()
    {
        var act = () => new ServiceCollection().AddGoogleCloudStorage(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
