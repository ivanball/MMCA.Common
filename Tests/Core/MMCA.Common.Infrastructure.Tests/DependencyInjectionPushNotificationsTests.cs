using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests;

public sealed class DependencyInjectionPushNotificationsTests
{
    private static IConfiguration CreateConfigurationWithRedis(string? redisConnectionString = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["PushNotifications:Enabled"] = "true",
            ["PushNotifications:HubPath"] = "/hubs/test",
        };

        if (redisConnectionString is not null)
        {
            configData["ConnectionStrings:redis"] = redisConnectionString;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

    // ── AddPushNotifications registers PushNotificationSettings ──
    [Fact]
    public void AddPushNotifications_RegistersPushNotificationSettings()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IConfigureOptions<PushNotificationSettings>));

        descriptor.Should().NotBeNull();
    }

    // ── AddPushNotifications registers SignalRPushNotificationSender ──
    [Fact]
    public void AddPushNotifications_RegistersSignalRPushNotificationSender()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IPushNotificationSender));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<SignalRPushNotificationSender>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    // ── AddPushNotifications registers ClaimBasedUserIdProvider ──
    [Fact]
    public void AddPushNotifications_RegistersClaimBasedUserIdProvider()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Microsoft.AspNetCore.SignalR.IUserIdProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    // ── AddPushNotifications registers SignalR hub services ──
    [Fact]
    public void AddPushNotifications_RegistersSignalRServices()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        // SignalR registration adds multiple services; check for the hub route marker
        services.Should().Contain(d =>
            d.ServiceType.FullName != null &&
            d.ServiceType.FullName.Contains("SignalR", StringComparison.OrdinalIgnoreCase));
    }

    // ── AddPushNotifications without Redis: no error ──
    [Fact]
    public void AddPushNotifications_WithoutRedis_DoesNotThrow()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        var act = () => services.AddPushNotifications(config);

        act.Should().NotThrow();
    }

    // ── AddPushNotifications returns service collection for chaining ──
    [Fact]
    public void AddPushNotifications_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        IServiceCollection result = services.AddPushNotifications(config);

        result.Should().BeSameAs(services);
    }

    // ── AddPushNotifications binds settings from configuration ──
    [Fact]
    public void AddPushNotifications_BindsSettingsFromConfiguration()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        PushNotificationSettings settings = sp.GetRequiredService<IOptions<PushNotificationSettings>>().Value;

        settings.Enabled.Should().BeTrue();
        settings.HubPath.Should().Be("/hubs/test");
    }

    // ── AddPushNotifications registers IPushNotificationSettings singleton ──
    [Fact]
    public void AddPushNotifications_RegistersIPushNotificationSettingsSingleton()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IPushNotificationSettings));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    // ── AddPushNotifications with Redis connection string configures backplane ──
    [Fact]
    public void AddPushNotifications_WithRedisConnectionString_DoesNotThrow()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis("localhost:6379");

        var act = () => services.AddPushNotifications(config);

        act.Should().NotThrow();
    }
}
