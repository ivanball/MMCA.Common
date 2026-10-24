using AwesomeAssertions;
using Azure.Storage.Blobs;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Azure.Storage;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Azure.Tests;

/// <summary>
/// The Azure storage and native-push registrations: each swaps the inert core default only when its
/// section is complete, and once it has, the core startup tripwire is satisfied.
/// </summary>
public sealed class AzureServiceCollectionExtensionsTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Type? ImplementationOf<TService>(IServiceCollection services) =>
        services.LastOrDefault(d => d.ServiceType == typeof(TService))?.ImplementationType;

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "https://acct.blob.core.windows.net")]
    [InlineData("avatars", null)]
    [InlineData("avatars", "")]
    public void AddAzureBlobFileStorage_IncompleteSection_IsANoOp(string? container, string? serviceUri)
    {
        var services = new ServiceCollection();

        services.AddAzureBlobFileStorage(ConfigurationWith(new()
        {
            ["FileStorage:ContainerName"] = container,
            ["FileStorage:ServiceUri"] = serviceUri,
        }));

        ImplementationOf<IFileStorageService>(services).Should().BeNull();
    }

    [Theory]
    [InlineData("FileStorage:ServiceUri", "https://acct.blob.core.windows.net")]
    [InlineData("FileStorage:ConnectionString", "UseDevelopmentStorage=true")]
    public void AddAzureBlobFileStorage_CompleteSection_RegistersTheBlobService(string key, string value)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddAzureBlobFileStorage(ConfigurationWith(new() { ["FileStorage:ContainerName"] = "avatars", [key] = value }));

        ImplementationOf<IFileStorageService>(services).Should().Be<AzureBlobFileStorageService>();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<BlobContainerClient>().Name.Should().Be("avatars");
        provider.GetRequiredService<IOptions<AzureBlobFileStorageSettings>>().Value.ContainerName.Should().Be("avatars");
    }

    [Theory]
    [InlineData("false", "Endpoint=sb://hub.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dg==", "hub")]
    [InlineData("true", "", "hub")]
    [InlineData("true", "Endpoint=sb://hub.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dg==", "")]
    public void AddAzureNotificationHubsPush_DisabledOrIncomplete_IsANoOp(string enabled, string connectionString, string hubName)
    {
        var services = new ServiceCollection();

        services.AddAzureNotificationHubsPush(ConfigurationWith(new()
        {
            ["NativePush:Enabled"] = enabled,
            ["NativePush:ConnectionString"] = connectionString,
            ["NativePush:HubName"] = hubName,
        }));

        ImplementationOf<INativePushSender>(services).Should().BeNull();
        ImplementationOf<IPushDeviceRegistrar>(services).Should().BeNull();
    }

    [Fact]
    public void AddAzureNotificationHubsPush_EnabledAndComplete_RegistersTheHubPair()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddAzureNotificationHubsPush(ConfigurationWith(new()
        {
            ["NativePush:Enabled"] = "true",
            ["NativePush:ConnectionString"] = "Endpoint=sb://hub.servicebus.windows.net/;SharedAccessKeyName=DefaultFullSharedAccessSignature;SharedAccessKey=dGVzdA==",
            ["NativePush:HubName"] = "mmca-hub",
        }));

        ImplementationOf<INativePushSender>(services).Should().Be<AzureNotificationHubNativePushSender>();
        ImplementationOf<IPushDeviceRegistrar>(services).Should().Be<AzureNotificationHubDeviceRegistrar>();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<INotificationHubClient>().Should().NotBeNull();
    }

    [Fact]
    public void Registrations_RejectANullConfiguration()
    {
        var services = new ServiceCollection();

        var storage = () => services.AddAzureBlobFileStorage(null!);
        var push = () => services.AddAzureNotificationHubsPush(null!);

        storage.Should().Throw<ArgumentNullException>();
        push.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CoreTripwire_IsSatisfied_OnceTheAdaptersAreRegistered()
    {
        // The end-to-end proof of the guard's silent case: the same configuration that fails a host
        // without the adapter calls validates cleanly with them.
        var configuration = ConfigurationWith(new()
        {
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=:memory:",
            ["FileStorage:ContainerName"] = "avatars",
            ["FileStorage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["NativePush:Enabled"] = "true",
            ["NativePush:ConnectionString"] = "Endpoint=sb://hub.servicebus.windows.net/;SharedAccessKeyName=DefaultFullSharedAccessSignature;SharedAccessKey=dGVzdA==",
            ["NativePush:HubName"] = "mmca-hub",
        });

        var withoutAdapters = BuildProvider(configuration, registerAdapters: false);
        var withAdapters = BuildProvider(configuration, registerAdapters: true);

        var failing = () => withoutAdapters.GetRequiredService<IStartupValidator>().Validate();
        var passing = () => withAdapters.GetRequiredService<IStartupValidator>().Validate();

        failing.Should().Throw<OptionsValidationException>().WithMessage("*AddAzureBlobFileStorage*");
        passing.Should().NotThrow();
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration, bool registerAdapters)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration);
        if (registerAdapters)
        {
            services.AddAzureBlobFileStorage(configuration);
            services.AddAzureNotificationHubsPush(configuration);
        }

        return services.BuildServiceProvider();
    }
}
