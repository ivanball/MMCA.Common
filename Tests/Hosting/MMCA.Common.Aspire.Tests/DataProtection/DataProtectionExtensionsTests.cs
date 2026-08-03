using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Tests.DataProtection;

/// <summary>
/// Guards the two-stage configuration gate on <c>AddCommonDataProtection</c>: no
/// <c>DataProtection:BlobStorageUri</c> means the host keeps the in-memory default and takes no
/// Azure dependency at startup, while a configured URI swaps the key-ring repository for the blob
/// one so every replica shares a key ring. Both tests assert on the resolved options of a built
/// service provider, so neither needs Azure credentials nor any network access: the blob client is
/// constructed lazily by the repository, never at registration time.
/// </summary>
public sealed class DataProtectionExtensionsTests
{
    [Fact]
    public void AddCommonDataProtection_WithoutBlobStorageUri_RegistersNothing()
    {
        var builder = BuilderWith([]);

        builder.AddCommonDataProtection();

        builder.Services.Any(d => d.ServiceType == typeof(IDataProtectionProvider))
            .Should().BeFalse(
                because: "an unconfigured host must not pull in DataProtection services at all, so local development and tests stay Azure-free");

        builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<KeyManagementOptions>>()
            .Value.XmlRepository
            .Should().BeNull(
                because: "no key-ring repository is configured when the gate key is absent");
    }

    [Fact]
    public void AddCommonDataProtection_WithBlobStorageUri_PersistsTheKeyRingToBlobStorage()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:BlobStorageUri"] = "https://example.blob.core.windows.net/dataprotection/keys.xml",
            ["DataProtection:ApplicationName"] = "mmca-data-protection-tests",
        });

        builder.AddCommonDataProtection();

        var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator
            .Should().Be(
                "mmca-data-protection-tests",
                because: "the discriminator is what keeps two applications sharing one blob or directory from reading each other's keys");

        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;

        repository.Should().NotBeNull(
            because: "a configured blob URI must replace the in-memory default key-ring repository");

        repository!.GetType().Assembly.GetName().Name
            .Should().Be(
                "Azure.Extensions.AspNetCore.DataProtection.Blobs",
                because: "the replacement has to be the blob repository specifically: any other repository leaves each replica with its own keys");
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
