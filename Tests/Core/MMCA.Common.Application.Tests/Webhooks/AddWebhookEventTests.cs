using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <c>AddWebhookEvent&lt;TEvent&gt;(name)</c> (rules 35.1 to 35.3): one singleton fan-out handler per
/// event type, one catalog entry per (type, name), and registration-time refusal of bad or
/// conflicting names.
/// </summary>
public sealed class AddWebhookEventTests
{
    [Fact]
    public void Registers_FanOutAsIntegrationHandler()
    {
        var services = new ServiceCollection();

        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(IIntegrationEventHandler<WebhookRegistrationProbeEvent>)).Which;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton, "integration event handlers are singletons that open their own scope");
        descriptor.ImplementationType.Should().Be<WebhookFanOutHandler<WebhookRegistrationProbeEvent>>();
    }

    [Fact]
    public void Registers_TheEventInTheCatalog()
    {
        var services = new ServiceCollection();
        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<WebhookEventCatalog>();

        provider.GetServices<WebhookEventRegistration>().Should().ContainSingle()
            .Which.Should().Be(new WebhookEventRegistration(typeof(WebhookRegistrationProbeEvent), "ticket.opened"));
        catalog.EventTypes.Should().ContainSingle().Which.Should().Be("ticket.opened");
        catalog.Contains("ticket.opened").Should().BeTrue();
        catalog.Contains("ticket.closed").Should().BeFalse();
        catalog.TryGetEventType(typeof(WebhookRegistrationProbeEvent), out var eventType).Should().BeTrue();
        eventType.Should().Be("ticket.opened");
        catalog.TryGetEventType(typeof(WebhookRegistrationOtherProbeEvent), out _).Should().BeFalse();
    }

    [Fact]
    public void Catalog_IsOneSingletonOverEveryRegistration()
    {
        var services = new ServiceCollection();
        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");
        services.AddWebhookEvent<WebhookRegistrationOtherProbeEvent>("ticket.closed");

        using var provider = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(WebhookEventCatalog)).Should().Be(1);
        services.Single(d => d.ServiceType == typeof(WebhookEventCatalog)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        provider.GetRequiredService<WebhookEventCatalog>().Should().BeSameAs(provider.GetRequiredService<WebhookEventCatalog>());
        provider.GetRequiredService<WebhookEventCatalog>().EventTypes.Should().BeEquivalentTo("ticket.opened", "ticket.closed");
    }

    [Fact]
    public void Catalog_WithNothingRegistered_IsEmpty()
    {
        var catalog = new WebhookEventCatalog([]);

        catalog.EventTypes.Should().BeEmpty();
        catalog.Contains("ticket.opened").Should().BeFalse();
        catalog.TryGetEventType(typeof(WebhookRegistrationProbeEvent), out _).Should().BeFalse();
    }

    [Fact]
    public void SameTypeAndName_Twice_IsNoOp()
    {
        var services = new ServiceCollection();

        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");
        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");

        services.Count(d => d.ServiceType == typeof(IIntegrationEventHandler<WebhookRegistrationProbeEvent>)).Should().Be(1);
        using var provider = services.BuildServiceProvider();
        provider.GetServices<WebhookEventRegistration>().Should().ContainSingle();
        provider.GetRequiredService<WebhookEventCatalog>().EventTypes.Should().ContainSingle();
        provider.GetServices<IIntegrationEventHandler<WebhookRegistrationProbeEvent>>().Should().ContainSingle();
    }

    [Fact]
    public void SameNameOtherType_Throws()
    {
        var services = new ServiceCollection();
        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");

        Action act = () => services.AddWebhookEvent<WebhookRegistrationOtherProbeEvent>("ticket.opened");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(WebhookRegistrationProbeEvent)}*")
            .WithMessage($"*{nameof(WebhookRegistrationOtherProbeEvent)}*");
    }

    [Fact]
    public void SameTypeOtherName_Throws()
    {
        var services = new ServiceCollection();
        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened");

        Action act = () => services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.created");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ticket.opened*")
            .WithMessage("*ticket.created*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("Ticket.Opened")]
    [InlineData("a..b")]
    [InlineData("1ticket")]
    [InlineData("ticket.")]
    [InlineData(".ticket")]
    [InlineData("ticket opened")]
    [InlineData("ticket.-opened")]
    public void InvalidName_Throws(string name)
    {
        var services = new ServiceCollection();

        Action act = () => services.AddWebhookEvent<WebhookRegistrationProbeEvent>(name);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NameOver100Chars_Throws()
    {
        var services = new ServiceCollection();
        var name = "a" + new string('b', 100);

        Action act = () => services.AddWebhookEvent<WebhookRegistrationProbeEvent>(name);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ticket.opened")]
    [InlineData("order-paid")]
    [InlineData("invoice_sent.v2")]
    [InlineData("a1.b2-c3_d4")]
    public void ValidName_Registers(string name)
    {
        var services = new ServiceCollection();

        services.AddWebhookEvent<WebhookRegistrationProbeEvent>(name);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<WebhookEventCatalog>().Contains(name).Should().BeTrue();
    }

    [Fact]
    public void NameOf100Chars_Registers()
    {
        var services = new ServiceCollection();
        var name = "a" + new string('b', 99);

        Action act = () => services.AddWebhookEvent<WebhookRegistrationProbeEvent>(name);

        act.Should().NotThrow();
    }

    [Fact]
    public void ReturnsTheServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        services.AddWebhookEvent<WebhookRegistrationProbeEvent>("ticket.opened").Should().BeSameAs(services);
    }
}

// -- Test types --
public sealed record WebhookRegistrationProbeEvent(int TicketId) : BaseIntegrationEvent;

public sealed record WebhookRegistrationOtherProbeEvent(int TicketId) : BaseIntegrationEvent;
