using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Webhooks;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// The <c>MMCA.Webhooks</c> client composed with host-wide client defaults (review rulings H1 and M1):
/// whichever order a host registers <c>ConfigureHttpClientDefaults</c> and <c>AddWebhooks</c> in, no
/// default delegating handler (a resilience pipeline, service discovery, any host-wide handler) wraps a
/// delivery, no default primary handler replaces the address-checking <see cref="SocketsHttpHandler"/>,
/// and the client stack has no logging handler, so the target URL is never logged by it. The
/// internal-command queue stays the only retry and the only failure policy.
/// </summary>
public sealed class WebhookClientCompositionTests
{
    private const string ClientName = "MMCA.Webhooks";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostDefaultHandlers_NeverWrapTheWebhookClient(bool defaultsFirst)
    {
        using var provider = Build(
            defaultsFirst,
            services => services.ConfigureHttpClientDefaults(builder => builder.AddHttpMessageHandler(() => new MarkerHandler())));

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName));

        chain.Should().NotContain(
            handler => handler is MarkerHandler,
            "a host-wide delegating handler (a resilience pipeline above all) must never wrap a webhook delivery");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostDefaultHandlers_StillWrapOtherClients(bool defaultsFirst)
    {
        // The control for the theory above: the marker default is live, it only skips the webhook client.
        using var provider = Build(
            defaultsFirst,
            services => services.ConfigureHttpClientDefaults(builder => builder.AddHttpMessageHandler(() => new MarkerHandler())));

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("SomeOtherClient"));

        chain.Should().Contain(handler => handler is MarkerHandler);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostDefaultPrimaryHandler_NeverReplacesTheWebhookHandler(bool defaultsFirst)
    {
        using var provider = Build(
            defaultsFirst,
            services => services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => new MarkerPrimaryHandler())));

        var innermost = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName))[^1];

        var primary = innermost.Should().BeOfType<SocketsHttpHandler>(
            "the connect-time address check lives on the webhook client's own SocketsHttpHandler").Which;
        primary.ConnectCallback.Should().NotBeNull("rule 35.23: every connection is checked at connect time");
        primary.UseProxy.Should().BeFalse();
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.UseCookies.Should().BeFalse();
        primary.ActivityHeadersPropagator.Should().BeNull(
            "no traceparent, tracestate or baggage header goes to a third-party receiver");
    }

    [Fact]
    public void TheWebhookClient_HasNoLoggingHandler()
    {
        using var provider = Build(defaultsFirst: true, _ => { });

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName));

        chain.Select(handler => handler.GetType().Name).Should().NotContain(
            name => name.Contains("Logging", StringComparison.Ordinal),
            "the client stack never logs the target URL, which often carries the receiver's token in its path");
    }

    [Fact]
    public void OtherClients_KeepTheirLoggingHandlers()
    {
        // The control for the fact above: logging is registered and does reach a client that is not the webhook one.
        using var provider = Build(defaultsFirst: true, _ => { });

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("SomeOtherClient"));

        chain.Select(handler => handler.GetType().Name).Should().Contain(name => name.Contains("Logging", StringComparison.Ordinal));
    }

    /// <summary>Every handler from the outermost to the primary one, following each <see cref="DelegatingHandler.InnerHandler"/>.</summary>
    private static List<HttpMessageHandler> Chain(HttpMessageHandler outermost)
    {
        List<HttpMessageHandler> chain = [outermost];
        var current = outermost;
        while (current is DelegatingHandler { InnerHandler: { } inner })
        {
            chain.Add(inner);
            current = inner;
        }

        return chain;
    }

    private static ServiceProvider Build(bool defaultsFirst, Action<IServiceCollection> configureDefaults)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        if (defaultsFirst)
        {
            configureDefaults(services);
            services.AddWebhooks(configuration);
        }
        else
        {
            services.AddWebhooks(configuration);
            configureDefaults(services);
        }

        services.AddHttpClient("SomeOtherClient");
        return services.BuildServiceProvider();
    }

    /// <summary>Stands in for any host-wide delegating handler (the standard resilience handler, service discovery).</summary>
    private sealed class MarkerHandler : DelegatingHandler;

    /// <summary>Stands in for a host-wide primary handler.</summary>
    private sealed class MarkerPrimaryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
