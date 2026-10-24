using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Infrastructure.Storage;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Configuration;

/// <summary>
/// The Infrastructure startup tripwire: a configured <c>FileStorage</c> section or an enabled
/// <c>NativePush</c> section whose service still resolves to the inert core default fails the host,
/// naming the adapter call; a real implementation, or no configuration, stays silent.
/// </summary>
public sealed class CloudAdapterTripwireTests
{
    private static ValidateOptionsResult Validate(
        Dictionary<string, string?> settings,
        IFileStorageService? fileStorage = null,
        INativePushSender? nativePush = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddTransient<IFileStorageService, NullFileStorageService>();
        services.AddTransient<INativePushSender, NullNativePushSender>();
        if (fileStorage is not null)
        {
            services.AddSingleton(fileStorage);
        }

        if (nativePush is not null)
        {
            services.AddSingleton(nativePush);
        }

        using var provider = services.BuildServiceProvider();
        return new CloudAdapterTripwire(provider).Validate(Options.DefaultName, new CloudAdapterTripwireOptions());
    }

    [Fact]
    public void FileStorageConfigured_WithTheNullDefault_FailsNamingTheAdapterCall()
    {
        var result = Validate(new() { ["FileStorage:ContainerName"] = "avatars", ["FileStorage:ServiceUri"] = "https://acct.blob.core.windows.net" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("FileStorage").And.Contain("AddAzureBlobFileStorage(configuration)").And.Contain("MMCA.Common.Azure");
    }

    [Fact]
    public void FileStorageConfigured_WithAnAdapter_Succeeds() =>
        Validate(
            new() { ["FileStorage:ContainerName"] = "avatars", ["FileStorage:ServiceUri"] = "https://acct.blob.core.windows.net" },
            fileStorage: Mock.Of<IFileStorageService>())
            .Succeeded.Should().BeTrue();

    [Fact]
    public void FileStorageWithOnlyBlankPlaceholders_StaysSilent() =>
        Validate(new() { ["FileStorage:ContainerName"] = string.Empty })
            .Succeeded.Should().BeTrue("an appsettings.json that ships blank placeholders has not configured storage");

    [Fact]
    public void NativePushEnabled_WithTheNullDefault_FailsNamingTheAdapterCall()
    {
        var result = Validate(new() { ["NativePush:Enabled"] = "true" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("NativePush:Enabled").And.Contain("AddAzureNotificationHubsPush(configuration)");
    }

    [Fact]
    public void NativePushEnabled_WithAnAdapter_Succeeds() =>
        Validate(new() { ["NativePush:Enabled"] = "true" }, nativePush: Mock.Of<INativePushSender>())
            .Succeeded.Should().BeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("not-a-bool")]
    public void NativePushNotEnabled_StaysSilent(string enabled) =>
        Validate(new() { ["NativePush:Enabled"] = enabled, ["NativePush:HubName"] = "hub" })
            .Succeeded.Should().BeTrue("the section ships disabled until the hub has platform credentials");

    [Fact]
    public void NothingConfigured_StaysSilent() =>
        Validate([]).Succeeded.Should().BeTrue();

    [Fact]
    public void BothMisconfigured_ReportsBothInOneFailure() =>
        Validate(new() { ["FileStorage:ConnectionString"] = "UseDevelopmentStorage=true", ["NativePush:Enabled"] = "true" })
            .Failures.Should().HaveCount(2);

    [Fact]
    public void NoConfigurationRegistered_Skips()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        new CloudAdapterTripwire(provider).Validate(Options.DefaultName, new CloudAdapterTripwireOptions())
            .Skipped.Should().BeTrue();
    }

    [Fact]
    public void AddInfrastructure_RegistersTheTripwireToValidateOnStart()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());

        services.Should().Contain(d =>
            d.ServiceType == typeof(IValidateOptions<CloudAdapterTripwireOptions>) && d.ImplementationType == typeof(CloudAdapterTripwire));
    }
}
