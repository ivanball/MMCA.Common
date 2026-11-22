using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Notifications.Live;
using MMCA.Common.Infrastructure.Notifications.Push;
using Moq;

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

    // ── AddPushNotifications registers SignalRLiveChannelPublisher ──
    [Fact]
    public void AddPushNotifications_RegistersSignalRLiveChannelPublisher()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ILiveChannelPublisher));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<SignalRLiveChannelPublisher>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    // ── AddPushNotifications binds ChannelKeyPattern from configuration ──
    [Fact]
    public void AddPushNotifications_BindsChannelKeyPatternFromConfiguration()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["PushNotifications:Enabled"] = "true",
            ["PushNotifications:ChannelKeyPattern"] = "^room:[a-z]+$",
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        services.AddPushNotifications(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        PushNotificationSettings settings = sp.GetRequiredService<IOptions<PushNotificationSettings>>().Value;

        settings.ChannelKeyPattern.Should().Be("^room:[a-z]+$");
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

    // L182: AddSignalR registers its DefaultUserIdProvider first, so the claim-based provider must
    // REPLACE it; a TryAdd leaves SignalR's default as the one that resolves.
    [Fact]
    public void AddPushNotifications_ResolvesClaimBasedUserIdProvider()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis();

        services.AddPushNotifications(config);

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<Microsoft.AspNetCore.SignalR.IUserIdProvider>()
            .Should().BeOfType<ClaimBasedUserIdProvider>("a principal carrying only the raw sub claim must still map to its user");
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

    // ── AddPushNotifications with Redis connection string configures backplane ──
    [Fact]
    public void AddPushNotifications_WithRedisConnectionString_DoesNotThrow()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateConfigurationWithRedis("localhost:6379");

        var act = () => services.AddPushNotifications(config);

        act.Should().NotThrow();
    }

    // -- B6: one startup Warning naming IChannelJoinAuthorizer when none is registered --
    [Fact]
    public async Task AddPushNotifications_WithNoChannelJoinAuthorizer_LogsOneStartupWarningNamingTheInterface()
    {
        var warnings = await StartAndCollectWarningsAsync(registerAuthorizer: false);

        warnings.Should().ContainSingle(message => message.Contains(nameof(IChannelJoinAuthorizer), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddPushNotifications_CalledTwiceWithNoChannelJoinAuthorizer_StillLogsTheWarningOnce()
    {
        var warnings = await StartAndCollectWarningsAsync(registerAuthorizer: false, registrations: 2);

        warnings.Count(message => message.Contains(nameof(IChannelJoinAuthorizer), StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task AddPushNotifications_WithAChannelJoinAuthorizer_LogsNoWarningAboutIt()
    {
        var warnings = await StartAndCollectWarningsAsync(registerAuthorizer: true);

        warnings.Should().NotContain(message => message.Contains(nameof(IChannelJoinAuthorizer), StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs every registered hosted service's start the way the host does at startup and returns the
    /// rendered Warning-or-above lines. The authorizer, when registered, is added AFTER
    /// <c>AddPushNotifications</c>, the order a host is free to use.
    /// </summary>
    private static async Task<List<string>> StartAndCollectWarningsAsync(bool registerAuthorizer, int registrations = 1)
    {
        var provider = new WarningCollectingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(provider));
        IConfiguration config = CreateConfigurationWithRedis();
        for (var i = 0; i < registrations; i++)
        {
            services.AddPushNotifications(config);
        }

        if (registerAuthorizer)
        {
            services.AddSingleton(Mock.Of<IChannelJoinAuthorizer>());
        }

        await using ServiceProvider sp = services.BuildServiceProvider();
        var hostedServices = sp.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(TestContext.Current.CancellationToken);
        }

        foreach (var hostedService in hostedServices)
        {
            await hostedService.StopAsync(TestContext.Current.CancellationToken);
        }

        return [.. provider.Warnings];
    }

    private sealed class WarningCollectingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new WarningCollectingLogger(Warnings);

        public void Dispose()
        {
            // Nothing to release.
        }

        private sealed class WarningCollectingLogger(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    warnings.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
