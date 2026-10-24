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
/// The Infrastructure startup tripwire: an enabled <c>NativePush</c> section whose sender still
/// resolves to the inert core default fails the host, naming the adapter call; a real implementation,
/// or no configuration, stays silent. A <c>FileStorage</c> section never trips it.
/// </summary>
public sealed class CloudAdapterTripwireTests
{
    private static ValidateOptionsResult Validate(
        Dictionary<string, string?> settings,
        INativePushSender? nativePush = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddTransient<IFileStorageService, NullFileStorageService>();
        services.AddTransient<INativePushSender, NullNativePushSender>();
        if (nativePush is not null)
        {
            services.AddSingleton(nativePush);
        }

        using var provider = services.BuildServiceProvider();
        return new CloudAdapterTripwire(provider).Validate(Options.DefaultName, new CloudAdapterTripwireOptions());
    }

    [Fact]
    public void FileStorageConfigured_WithTheNullDefault_StaysSilent() =>
        Validate(new() { ["FileStorage:ContainerName"] = "avatars", ["FileStorage:ConnectionString"] = "UseDevelopmentStorage=true" })
            .Succeeded.Should().BeTrue(
                "a FileStorage section is shared configuration (one user-secrets id across every service of a solution), not a declaration that this host stores files");

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
    public void FileStorageAlongsideEnabledNativePush_ReportsOnlyNativePush() =>
        Validate(new() { ["FileStorage:ConnectionString"] = "UseDevelopmentStorage=true", ["NativePush:Enabled"] = "true" })
            .Failures.Should().ContainSingle().Which.Should().Contain("NativePush:Enabled");

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
