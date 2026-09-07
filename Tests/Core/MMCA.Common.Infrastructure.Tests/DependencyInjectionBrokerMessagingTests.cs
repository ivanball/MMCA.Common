using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Persistence.Inbox;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// Registration-level tests for <c>AddBrokerMessaging</c>. They inspect the
/// <see cref="ServiceCollection"/> rather than building a provider: the broker branch registers
/// MassTransit and an EF-backed inbox store whose dependencies a unit test has no business
/// standing up, and what is under test here is which descriptors land, not what they resolve to.
/// </summary>
public sealed class DependencyInjectionBrokerMessagingTests
{
    private static IConfiguration ConfigurationFor(string provider, bool enableInbox) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = provider,
                ["MessageBus:EnableInbox"] = enableInbox ? "true" : "false",
                ["MessageBus:ConnectionString"] = "amqp://guest:guest@localhost:5672",
            })
            .Build();

    private static bool HasHostedService<T>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T));

    private static Type? InboxStoreImplementation(IServiceCollection services) =>
        services.FirstOrDefault(d => d.ServiceType == typeof(IInboxStore))?.ImplementationType;

    [Fact]
    public void AddBrokerMessaging_InboxDisabled_RegistersNoOpStoreAndTheLoudWarningService()
    {
        var services = new ServiceCollection();

        services.AddBrokerMessaging(ConfigurationFor("RabbitMq", enableInbox: false));

        InboxStoreImplementation(services).Should().Be<NoOpInboxStore>();
        HasHostedService<InboxDisabledWarningService>(services).Should()
            .BeTrue("a silently disabled dedup store is indistinguishable from an enabled one until a duplicate reaches a customer");
    }

    [Fact]
    public void AddBrokerMessaging_InboxEnabled_RegistersEfStoreAndNoWarningService()
    {
        var services = new ServiceCollection();

        services.AddBrokerMessaging(ConfigurationFor("RabbitMq", enableInbox: true));

        InboxStoreImplementation(services).Should().Be<EfInboxStore>();
        HasHostedService<InboxDisabledWarningService>(services).Should()
            .BeFalse("nothing is off, so there is nothing to warn about");
    }

    [Fact]
    public void AddBrokerMessaging_InboxSettingOmitted_DefaultsToTheEfStoreUnderABroker()
    {
        // The default is what most hosts run, and at-least-once delivery without dedup is not a
        // default worth shipping: leaving MessageBus:EnableInbox unset must give a broker host the
        // real store, not the no-op one.
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "RabbitMq",
                ["MessageBus:ConnectionString"] = "amqp://guest:guest@localhost:5672",
            })
            .Build();

        services.AddBrokerMessaging(configuration);

        InboxStoreImplementation(services).Should().Be<EfInboxStore>();
        HasHostedService<InboxDisabledWarningService>(services).Should().BeFalse();
    }

    [Fact]
    public void AddBrokerMessaging_InProcessProvider_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddBrokerMessaging(ConfigurationFor("InProcess", enableInbox: false));

        services.Should().BeEmpty("the in-process provider short-circuits before touching the container");
    }

    [Fact]
    public void AddBrokerMessaging_EndpointPrefix_ReachesTheEndpointNameFormatter()
    {
        // The setting exists so several services can share one broker: if the prefix never reaches the
        // formatter, two services with the same consumer type name derive the same queue and become
        // competing consumers of each other's events.
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "RabbitMq",
                ["MessageBus:ConnectionString"] = "amqp://guest:guest@localhost:5672",
                ["MessageBus:EndpointPrefix"] = "store-catalog",
            })
            .Build();

        services.AddBrokerMessaging(configuration);

        var formatter = services
            .Select(d => d.ImplementationInstance)
            .OfType<IEndpointNameFormatter>()
            .Should().ContainSingle().Subject;

        formatter.Consumer<OrderPlacedConsumer>().Should().Be("store-catalog-order-placed");
    }

    [Fact]
    public void AddBrokerMessaging_PreserveDefaultEndpointNames_RegistersNoFormatter()
    {
        // The opt-out for an existing deployment: with no formatter registered MassTransit derives
        // the same queue and subscription names it did before 1.188.0, so an upgrade does not
        // strand in-flight messages on renamed endpoints.
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "RabbitMq",
                ["MessageBus:ConnectionString"] = "amqp://guest:guest@localhost:5672",
                ["MessageBus:PreserveDefaultEndpointNames"] = "true",
            })
            .Build();

        services.AddBrokerMessaging(configuration);

        services
            .Select(d => d.ImplementationInstance)
            .OfType<IEndpointNameFormatter>()
            .Should().BeEmpty("the opt-out must leave MassTransit's default naming untouched");
    }

    [Fact]
    public void AddBrokerMessaging_UnsetPrefix_FallsBackToTheApplicationNamespace()
    {
        // SEC-Common-53: an unset prefix no longer means "no prefix". The formatter carries the
        // resolved application namespace so two applications on one broker never share a queue.
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "RabbitMq",
                ["MessageBus:ConnectionString"] = "amqp://guest:guest@localhost:5672",
                ["Application:Namespace"] = "adc-conference",
            })
            .Build();

        services.AddBrokerMessaging(configuration);

        var formatter = services
            .Select(d => d.ImplementationInstance)
            .OfType<IEndpointNameFormatter>()
            .Should().ContainSingle().Subject;

        formatter.Consumer<OrderPlacedConsumer>().Should().Be("adc-conference-order-placed");
    }

    /// <summary>A consumer that exists only to be named by the endpoint name formatter.</summary>
    private sealed class OrderPlacedConsumer : IConsumer<OrderPlacedTestEvent>
    {
        public Task Consume(ConsumeContext<OrderPlacedTestEvent> context) => Task.CompletedTask;
    }

    /// <summary>The message the naming-only consumer above is closed over.</summary>
    public sealed record OrderPlacedTestEvent(int OrderId);
}
