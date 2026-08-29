using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Persistence.Outbox;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// Registration-level tests for the outbox gate in <c>AddInfrastructure</c>. The outbox costs a
/// table, two hosted services and a poll loop; an in-process host takes none of that unless it asks
/// for it, a broker host always gets it, and asking for a broker WITHOUT it fails at registration
/// rather than silently dropping every cross-service event.
/// </summary>
public sealed class DependencyInjectionOutboxGateTests
{
    private static IConfiguration ConfigurationWith(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=test;Database=test",
            ["Jwt:SecretForKey"] = "dGVzdGtleXRoYXRpc2xvbmdlbm91Z2hmb3JiYXNlNjQ=",
            ["Jwt:Issuer"] = "https://test",
            ["Jwt:Audience"] = "test",
            ["Outbox:DataSource"] = "SQLServer",
        };

        foreach (var (key, value) in overrides)
            settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static bool HasHostedService<T>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T));

    [Fact]
    public void AddInfrastructure_InProcessByDefault_RegistersNoOutboxServices()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(ConfigurationWith());

        HasHostedService<OutboxProcessor>(services).Should()
            .BeFalse("a single-process host dispatches events in-process, so the drain loop has nothing to carry");
        HasHostedService<OutboxCleanupService>(services).Should()
            .BeFalse("there are no rows to sweep");
        HasHostedService<OutboxDisabledNoticeService>(services).Should()
            .BeTrue("the changed delivery guarantee is stated once at startup rather than inferred from an absent service");
    }

    [Fact]
    public void AddInfrastructure_OutboxExplicitlyEnabled_RegistersProcessorAndCleanup()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(ConfigurationWith(("MessageBus:EnableOutbox", "true")));

        HasHostedService<OutboxProcessor>(services).Should().BeTrue();
        HasHostedService<OutboxCleanupService>(services).Should().BeTrue();
        HasHostedService<OutboxDisabledNoticeService>(services).Should().BeFalse();
    }

    [Fact]
    public void AddInfrastructure_BrokerProvider_RegistersTheOutboxWithoutBeingAsked()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(ConfigurationWith(("MessageBus:Provider", "RabbitMq")));

        HasHostedService<OutboxProcessor>(services).Should()
            .BeTrue("the outbox is a broker deployment's only publish path, so it is never opt-in there");
        HasHostedService<OutboxCleanupService>(services).Should().BeTrue();
    }

    [Fact]
    public void AddInfrastructure_BrokerProviderWithOutboxDisabled_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddInfrastructure(
            ConfigurationWith(("MessageBus:Provider", "RabbitMq"), ("MessageBus:EnableOutbox", "false")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EnableOutbox*RabbitMq*",
                because: "a broker with no outbox drops every integration event, and a silent drop is only ever found downstream");
    }

    [Fact]
    public void AddBrokerMessaging_WithOutboxDisabled_ThrowsToo()
    {
        // A service host can wire the broker without the full infrastructure registration; the guard
        // has to sit on both entry points or that host runs with no delivery channel at all.
        var services = new ServiceCollection();

        var act = () => services.AddBrokerMessaging(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "AzureServiceBus",
                ["MessageBus:EnableOutbox"] = "false",
                ["MessageBus:ConnectionString"] = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v",
            })
            .Build());

        act.Should().Throw<InvalidOperationException>().WithMessage("*EnableOutbox*");
    }
}
