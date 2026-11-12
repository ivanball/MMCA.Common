using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Webhooks;
using MMCA.Common.Infrastructure.Webhooks;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <c>AddWebhooks(configuration)</c> (design 35(d), rules 35.22 to 35.24): binds and validates the
/// <c>Webhooks</c> section, registers the store, service, validator, sender, protector, resolver,
/// catalog and clock with TryAdd (so a double registered first is kept), adds data protection and the
/// startup notice, and configures the <c>MMCA.Webhooks</c> client's primary handler to follow no
/// redirect, use no proxy, keep no cookies and check every address at connect time.
/// </summary>
public sealed class AddWebhooksTests
{
    private const string ClientName = "MMCA.Webhooks";

    [Fact]
    public void BindsAndValidatesSettings()
    {
        using var provider = Build(new()
        {
            ["Webhooks:Enabled"] = "true",
            ["Webhooks:DataSourceName"] = "Integrations",
            ["Webhooks:TimeoutSeconds"] = "7",
            ["Webhooks:MaxPayloadBytes"] = "2048",
            ["Webhooks:PreviousSecretGraceHours"] = "0",
            ["Webhooks:MaxSubscriptionsPerTenant"] = "3",
            ["Webhooks:AllowInsecureTargetsInDevelopment"] = "true",
        });

        var settings = provider.GetRequiredService<IOptions<WebhookSettings>>().Value;

        settings.Enabled.Should().BeTrue();
        settings.DataSourceName.Should().Be("Integrations");
        settings.TimeoutSeconds.Should().Be(7);
        settings.MaxPayloadBytes.Should().Be(2048);
        settings.PreviousSecretGraceHours.Should().Be(0);
        settings.MaxSubscriptionsPerTenant.Should().Be(3);
        settings.AllowInsecureTargetsInDevelopment.Should().BeTrue();
    }

    [Fact]
    public void AnAbsentSection_BindsTheDocumentedDefaults()
    {
        using var provider = Build([]);

        var settings = provider.GetRequiredService<IOptions<WebhookSettings>>().Value;

        settings.Enabled.Should().BeFalse("calling AddWebhooks alone turns nothing on");
        settings.DataSourceName.Should().Be("Default");
        settings.TimeoutSeconds.Should().Be(10);
        settings.MaxPayloadBytes.Should().Be(65536);
        settings.PreviousSecretGraceHours.Should().Be(24);
        settings.MaxSubscriptionsPerTenant.Should().Be(25);
        settings.AllowInsecureTargetsInDevelopment.Should().BeFalse();
    }

    [Theory]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "31")]
    [InlineData("MaxPayloadBytes", "1023")]
    [InlineData("MaxPayloadBytes", "1048577")]
    [InlineData("PreviousSecretGraceHours", "-1")]
    [InlineData("PreviousSecretGraceHours", "169")]
    [InlineData("MaxSubscriptionsPerTenant", "0")]
    [InlineData("MaxSubscriptionsPerTenant", "501")]
    public void AValueOutsideItsRange_FailsValidationNamingTheKey(string key, string value)
    {
        using var provider = Build(new() { [$"Webhooks:{key}"] = value });

        var act = () => provider.GetRequiredService<IOptions<WebhookSettings>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void HandlerHasNoRedirectNoProxy()
    {
        using var provider = Build(new() { ["Webhooks:Enabled"] = "true" });

        HttpMessageHandler current = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);
        while (current is DelegatingHandler delegating)
        {
            current = delegating.InnerHandler!;
        }

        var primary = current.Should().BeOfType<SocketsHttpHandler>(
            "the connect-time address check lives on a SocketsHttpHandler ConnectCallback").Which;
        primary.AllowAutoRedirect.Should().BeFalse("a redirect is a refusal, never followed to an unchecked host");
        primary.UseProxy.Should().BeFalse("a proxy would connect on the sender's behalf and bypass the callback");
        primary.UseCookies.Should().BeFalse();
        primary.ConnectCallback.Should().NotBeNull("rule 35.23: every connection is checked at connect time");
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    public void NamedClientTimeout_IsTimeoutSeconds(string? configured, int expectedSeconds)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true" };
        if (configured is not null)
        {
            values["Webhooks:TimeoutSeconds"] = configured;
        }

        using var provider = Build(values);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds), "rule 35.24: HttpClient.Timeout is Webhooks:TimeoutSeconds");
    }

    [Fact]
    public void RegistersStoreProtectorValidatorSender()
    {
        var services = CreateServices([]);

        AssertRegistered<IWebhookSubscriptionStore>(services, typeof(EFWebhookSubscriptionStore), ServiceLifetime.Scoped);
        AssertRegistered<IWebhookSubscriptionService>(services, typeof(WebhookSubscriptionService), ServiceLifetime.Scoped);
        AssertRegistered<IWebhookTargetValidator>(services, typeof(DnsWebhookTargetValidator), ServiceLifetime.Scoped);
        AssertRegistered<IWebhookSender>(services, typeof(HttpWebhookSender), ServiceLifetime.Scoped);
        AssertRegistered<IWebhookSecretProtector>(services, typeof(DataProtectionWebhookSecretProtector), ServiceLifetime.Singleton);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IWebhookDnsResolver))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Should().ContainSingle(d => d.ServiceType == typeof(WebhookEventCatalog))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Should().Contain(d => d.ServiceType == typeof(TimeProvider));
        services.Should().Contain(d => d.ServiceType == typeof(IDataProtectionProvider), "AddWebhooks calls AddDataProtection");
        services.Should().Contain(
            d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(InsecureWebhookTargetsNoticeService));
    }

    [Fact]
    public void PreRegisteredDoubles_AreKept()
    {
        // A hand-rolled resolver: the interface is internal, and Moq cannot proxy an internal type
        // without an InternalsVisibleTo for DynamicProxyGenAssembly2.
        IWebhookDnsResolver resolver = new NoAddressResolver();
        var protector = Mock.Of<IWebhookSecretProtector>();
        var clock = new FixedClock();
        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        services.AddSingleton(protector);
        services.AddSingleton<TimeProvider>(clock);

        services.AddWebhooks(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWebhookDnsResolver>().Should().BeSameAs(resolver, "TryAddSingleton keeps an earlier registration");
        provider.GetRequiredService<IWebhookSecretProtector>().Should().BeSameAs(protector);
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    [Fact]
    public void NeverCalled_RegistersNoWebhookService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        provider.GetService<IWebhookSubscriptionService>().Should().BeNull();
        provider.GetService<IWebhookSender>().Should().BeNull();
        provider.GetService<IWebhookSubscriptionStore>().Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheEnabledFlag_ReachesTheService(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IWebhookSubscriptionStore>());
        services.AddSingleton(Mock.Of<ITenantContext>());
        services.AddWebhooks(Configuration(new() { ["Webhooks:Enabled"] = enabled ? "true" : "false" }));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionService>().IsEnabled.Should().Be(enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCatalog_IsRegisteredOnce_WhicheverCallRunsFirst(bool eventFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (eventFirst)
        {
            services.AddWebhookEvent<ProbeHappened>("probe.happened");
            services.AddWebhooks(Configuration([]));
        }
        else
        {
            services.AddWebhooks(Configuration([]));
            services.AddWebhookEvent<ProbeHappened>("probe.happened");
        }

        using var provider = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(WebhookEventCatalog)).Should().Be(1);
        provider.GetRequiredService<WebhookEventCatalog>().EventTypes.Should().Equal("probe.happened");
    }

    private static void AssertRegistered<TService>(ServiceCollection services, Type implementationType, ServiceLifetime lifetime)
    {
        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(TService), "registered once, with TryAdd").Which;
        descriptor.ImplementationType.Should().Be(implementationType);
        descriptor.Lifetime.Should().Be(lifetime);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceCollection CreateServices(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebhooks(Configuration(values));
        return services;
    }

    private static ServiceProvider Build(Dictionary<string, string?> values) => CreateServices(values).BuildServiceProvider();

    private sealed record ProbeHappened : BaseDomainEvent, IIntegrationEvent;

    private sealed class FixedClock : TimeProvider;

    private sealed class NoAddressResolver : IWebhookDnsResolver
    {
        public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<System.Net.IPAddress>());
    }
}
