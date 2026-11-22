using System.Xml.Linq;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Azure.Startup.Tests;

/// <summary>
/// Guards the two-stage configuration gate on <c>AddAzureDataProtection</c>: no
/// <c>DataProtection:BlobStorageUri</c> means the host keeps the in-memory default and takes no
/// Azure dependency at startup, while a configured URI swaps the key-ring repository for the blob
/// one so every replica shares a key ring. Both tests assert on the resolved options of a built
/// service provider, so neither needs Azure credentials nor any network access: the blob client is
/// constructed lazily by the repository, never at registration time.
/// </summary>
public sealed class AzureDataProtectionExtensionsTests
{
    [Fact]
    public void AddAzureDataProtection_WithoutBlobStorageUri_RegistersNothing()
    {
        var builder = BuilderWith([]);

        builder.AddAzureDataProtection();

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
    public void AddAzureDataProtection_WithBlobStorageUri_PersistsTheKeyRingToBlobStorage()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:BlobStorageUri"] = "https://example.blob.core.windows.net/dataprotection/keys.xml",
            ["DataProtection:ApplicationName"] = "mmca-data-protection-tests",
        });

        builder.AddAzureDataProtection();

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

    [Fact]
    public void AddAzureDataProtection_WithoutBlobStorageUri_LeavesNoReceipt()
    {
        var builder = BuilderWith([]);

        builder.AddAzureDataProtection();

        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that added nothing leaves no receipt, so a blob URI that appears later (a vault secret loaded after this call, for one) still trips the startup check");
    }

    [Fact]
    public void AddAzureDataProtection_WithBlobStorageUri_LeavesTheReceipt()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:BlobStorageUri"] = "https://example.blob.core.windows.net/dataprotection/keys.xml",
        });

        builder.AddAzureDataProtection();

        builder.Services.Should().ContainSingle(
            d => d.ImplementationInstance is HostIntegrationMarker
                && string.Equals(((HostIntegrationMarker)d.ImplementationInstance).Name, HostIntegrationNames.AzureDataProtection, StringComparison.Ordinal),
            because: "a call that registered the blob key ring leaves the receipt the startup check reads");
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    // -- The shared key ring with no at-rest key: one startup Warning outside Development --
    private const string BlobUri = "https://example.blob.core.windows.net/dataprotection/keys.xml";
    private const string AtRestKey = "DataProtection:KeyVaultKeyUri";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task AddAzureDataProtection_WithoutAnAtRestKey_OutsideDevelopment_WarnsOnceAtStartup(string environmentName)
    {
        var warnings = await StartupWarningsAsync(environmentName, new() { ["DataProtection:BlobStorageUri"] = BlobUri });

        warnings.Should().ContainSingle(w => w.Contains(AtRestKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddAzureDataProtection_WithAnAtRestKey_OutsideDevelopment_DoesNotWarn()
    {
        var warnings = await StartupWarningsAsync("Production", new()
        {
            ["DataProtection:BlobStorageUri"] = BlobUri,
            [AtRestKey] = "https://myvault.vault.azure.net/keys/dataprotection",
        });

        warnings.Should().NotContain(w => w.Contains(AtRestKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddAzureDataProtection_WithoutAnAtRestKey_InDevelopment_DoesNotWarn()
    {
        var warnings = await StartupWarningsAsync(Environments.Development, new() { ["DataProtection:BlobStorageUri"] = BlobUri });

        warnings.Should().NotContain(w => w.Contains(AtRestKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds and starts a real host over <c>AddAzureDataProtection</c> and returns every Warning it
    /// logged. The key ring is redirected to memory after the call (repository and encryptor), so
    /// starting the host reaches neither Blob Storage nor Key Vault.
    /// </summary>
    private static async Task<List<string>> StartupWarningsAsync(string environmentName, Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environmentName });
        builder.Configuration.AddInMemoryCollection(settings);
        var capture = new CapturingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);

        builder.AddAzureDataProtection();
        builder.Services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = new InMemoryXmlRepository();
            options.XmlEncryptor = null;
        });

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        return capture.Warnings;
    }

    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => [.. _elements];

        public void StoreElement(XElement element, string friendlyName) => _elements.Add(element);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];

        public List<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return [.. _warnings];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(string message)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    owner.Add(formatter(state, exception));
                }
            }
        }
    }
}
