using AwesomeAssertions;
using Google.Cloud.Kms.V1;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.DataProtection;
using MMCA.Common.Gcp.Tests.DataProtection;

namespace MMCA.Common.Gcp.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddGoogleCloudDataProtection</c>: no
/// <c>DataProtection:GcsObjectUri</c> means the host keeps the in-memory default, takes no Google
/// dependency and leaves no tripwire receipt, while a configured object swaps the key-ring
/// repository for the Cloud Storage one and, with <c>DataProtection:GcpKmsKeyName</c>, encrypts the
/// keys at rest with Cloud KMS. The clients are in-memory fakes registered first, so no test reaches
/// Google Cloud; the wire round trip is the emulator tier.
/// </summary>
public sealed class GoogleCloudDataProtectionExtensionsTests
{
    private const string ObjectUri = "gs://mmca-keys/adc/prod/keys.xml";
    private const string KmsKeyName = "projects/mmca-prod/locations/global/keyRings/dataprotection/cryptoKeys/key-ring";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddGoogleCloudDataProtection_WithoutAnObjectUri_RegistersNothing(string? objectUri)
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:GcsObjectUri"] = objectUri,
            ["DataProtection:GcpKmsKeyName"] = KmsKeyName,
        });

        builder.AddGoogleCloudDataProtection();

        builder.Services.Should().NotContain(
            d => d.ServiceType == typeof(IDataProtectionProvider),
            because: "an unconfigured host must not pull in DataProtection services at all, so local development and tests stay Google-free");
        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that registered nothing leaves no receipt");

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository.Should().BeNull();
    }

    [Fact]
    public void AddGoogleCloudDataProtection_WithAnObjectUri_PersistsTheKeyRingToCloudStorage()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:GcsObjectUri"] = ObjectUri,
            ["DataProtection:ApplicationName"] = "mmca-data-protection-tests",
        });
        builder.Services.AddSingleton<StorageClient>(new InMemoryGcsStorageClient());

        builder.AddGoogleCloudDataProtection();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator.Should().Be("mmca-data-protection-tests");

        var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = options.XmlRepository.Should().BeOfType<GcsXmlRepository>().Subject;
        repository.Location.Should().Be(new GcsObjectLocation("mmca-keys", "adc/prod/keys.xml"));
        options.XmlEncryptor.Should().BeNull("without DataProtection:GcpKmsKeyName the keys are stored as the framework writes them");

        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.GoogleCloudDataProtection);
    }

    [Fact]
    public void AddGoogleCloudDataProtection_WithoutAnApplicationName_UsesTheHostApplicationName()
    {
        var builder = BuilderWith(new() { ["DataProtection:GcsObjectUri"] = ObjectUri });
        builder.Services.AddSingleton<StorageClient>(new InMemoryGcsStorageClient());

        builder.AddGoogleCloudDataProtection();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator.Should().Be(builder.Environment.ApplicationName);
    }

    [Fact]
    public void AddGoogleCloudDataProtection_WithAKmsKey_EncryptsTheKeyRingAtRest()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:GcsObjectUri"] = ObjectUri,
            ["DataProtection:GcpKmsKeyName"] = KmsKeyName,
        });
        builder.Services.AddSingleton<StorageClient>(new InMemoryGcsStorageClient());
        builder.Services.AddSingleton<KeyManagementServiceClient>(new FakeKeyManagementServiceClient());

        builder.AddGoogleCloudDataProtection();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor
            .Should().BeOfType<GcpKmsXmlEncryptor>()
            .Which.KeyName.ToString().Should().Be(KmsKeyName);
    }

    [Theory]
    [InlineData("gs://mmca-keys")]
    [InlineData("https://storage.googleapis.com/mmca-keys/keys.xml")]
    public void AddGoogleCloudDataProtection_WithAMalformedObjectUri_ThrowsNamingTheKey(string objectUri)
    {
        var builder = BuilderWith(new() { ["DataProtection:GcsObjectUri"] = objectUri });

        var act = () => builder.AddGoogleCloudDataProtection();

        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:GcsObjectUri*");
        builder.Services.Should().NotContain(d => d.ImplementationInstance is HostIntegrationMarker);
    }

    [Theory]
    [InlineData("dataprotection")]
    [InlineData("projects/mmca-prod/locations/global/keyRings/dataprotection")]
    [InlineData("projects/mmca-prod/locations/global/keyRings/dataprotection/cryptoKeys/key-ring/cryptoKeyVersions/1")]
    public void AddGoogleCloudDataProtection_WithAMalformedKmsKeyName_ThrowsNamingTheKey(string kmsKeyName)
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:GcsObjectUri"] = ObjectUri,
            ["DataProtection:GcpKmsKeyName"] = kmsKeyName,
        });

        var act = () => builder.AddGoogleCloudDataProtection();

        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:GcpKmsKeyName*projects/*cryptoKeys/*");
    }

    [Fact]
    public void TwoHosts_OnOneObject_ShareTheKeyRing_AndStoreOnlyCiphertext()
    {
        // Two replicas: separate containers, one bucket object, one KMS key. A payload protected by
        // the first must unprotect on the second, and the object at rest holds no key in clear.
        var storage = new InMemoryGcsStorageClient();
        var kms = new FakeKeyManagementServiceClient();
        using var first = BuildHost(storage, kms);
        using var second = BuildHost(storage, kms);

        var protectedPayload = first.GetDataProtector("tests").Protect("hello");

        second.GetDataProtector("tests").Unprotect(protectedPayload).Should().Be("hello");
        var stored = storage.ContentOf("mmca-keys", "adc/prod/keys.xml");
        stored.Should().Contain("encryptedKey").And.Contain(KmsKeyName).And.NotContain("<masterKey");
        kms.DecryptRequests.Should().NotBeEmpty("the second host read the key by decrypting it");
    }

    private static ServiceProvider BuildHost(InMemoryGcsStorageClient storage, FakeKeyManagementServiceClient kms)
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:GcsObjectUri"] = ObjectUri,
            ["DataProtection:GcpKmsKeyName"] = KmsKeyName,
            ["DataProtection:ApplicationName"] = "mmca-shared",
        });
        builder.Services.AddSingleton<StorageClient>(storage);
        builder.Services.AddSingleton<KeyManagementServiceClient>(kms);
        builder.AddGoogleCloudDataProtection();
        return builder.Services.BuildServiceProvider();
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
