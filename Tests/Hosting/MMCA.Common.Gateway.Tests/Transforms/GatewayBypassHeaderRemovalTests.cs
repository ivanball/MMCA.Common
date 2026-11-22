using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.Gateway.Tests.Transforms;

/// <summary>
/// Pins item B4 end to end: a request proxied by a gateway composed with
/// <c>AddMmcaGateway</c> reaches the destination with neither the trusted-caller header nor the
/// synthetic-traffic header, under their default names and under configured ones, while every other
/// header still arrives and the gateway's own pipeline (where the edge limiter runs) still sees both.
/// <para>
/// The destination is an in-memory handler behind a host-supplied <see cref="IForwarderHttpClientFactory"/>
/// (the extension point a host's own forwarder composition uses), so the assertion is on the request the
/// destination actually received. The gateway itself listens on a dynamic loopback port (this test project
/// carries no TestServer package), the same throwaway-Kestrel pattern the Aspire warm-up tests use.
/// </para>
/// </summary>
public sealed class GatewayBypassHeaderRemovalTests
{
    private const string RoutePath = "/catalog/items";

    [Fact]
    public async Task ProxiedRequest_CarriesNeitherBypassHeader_UnderTheDefaultNames()
    {
        var destination = new RecordingDestination();
        var seenByPipeline = new ConcurrentBag<string>();
        await using var app = await StartGatewayAsync(destination, seenByPipeline, configuration: []);

        var status = await ProxyAsync(app, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Internal-Caller-Key"] = "trusted-secret-value-for-the-tests-000",
            ["X-Synthetic-Traffic-Key"] = "synthetic-secret-value-for-the-tests-0",
            ["X-Unrelated"] = "kept",
        });

        status.Should().Be(HttpStatusCode.OK);
        var received = destination.Requests.Should().ContainSingle().Which;
        received.Should().NotContainKey("X-Internal-Caller-Key");
        received.Should().NotContainKey("X-Synthetic-Traffic-Key");
        received.Should().ContainKey("X-Unrelated", "control: ordinary request headers are still forwarded");
        seenByPipeline.Should().Contain(["X-Internal-Caller-Key", "X-Synthetic-Traffic-Key"],
            "the gateway's own pipeline, where the edge limiter reads them, still sees both headers");
    }

    [Fact]
    public async Task ProxiedRequest_CarriesNeitherBypassHeader_UnderConfiguredNames()
    {
        var destination = new RecordingDestination();
        var seenByPipeline = new ConcurrentBag<string>();
        await using var app = await StartGatewayAsync(destination, seenByPipeline, configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GatewayRateLimiting:TrustedCallerHeaderName"] = "X-Edge-Proof",
            ["GatewayRateLimiting:SyntheticTrafficHeaderName"] = "X-Load-Proof",
        });

        var status = await ProxyAsync(app, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Edge-Proof"] = "trusted-secret-value-for-the-tests-000",
            ["X-Load-Proof"] = "synthetic-secret-value-for-the-tests-0",
            ["X-Unrelated"] = "kept",
        });

        status.Should().Be(HttpStatusCode.OK);
        var received = destination.Requests.Should().ContainSingle().Which;
        received.Should().NotContainKey("X-Edge-Proof");
        received.Should().NotContainKey("X-Load-Proof");
        received.Should().ContainKey("X-Unrelated");
        seenByPipeline.Should().Contain(["X-Edge-Proof", "X-Load-Proof"]);
    }

    private static async Task<WebApplication> StartGatewayAsync(
        RecordingDestination destination,
        ConcurrentBag<string> seenByPipeline,
        Dictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration);

        // Registered before AddReverseProxy, whose own factory registration is a TryAdd.
        builder.Services.AddSingleton<IForwarderHttpClientFactory>(destination);
        builder.Services.AddReverseProxy()
            .AddMmcaGateway(builder.Configuration)
            .LoadFromMemory(
                [
                    new RouteConfig
                    {
                        RouteId = "catalog",
                        ClusterId = "catalog",
                        Match = new RouteMatch { Path = "/catalog/{**rest}" },
                    },
                ],
                [
                    new ClusterConfig
                    {
                        ClusterId = "catalog",
                        Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                        {
                            ["d1"] = new DestinationConfig { Address = "http://catalog.internal/" },
                        },
                    },
                ]);

        var app = builder.Build();

        // Stands where the edge limiter runs: ahead of the proxy, reading the incoming request.
        app.Use(async (context, next) =>
        {
            foreach (var name in context.Request.Headers.Keys)
            {
                seenByPipeline.Add(name);
            }

            await next(context);
        });
        app.MapReverseProxy();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<HttpStatusCode> ProxyAsync(WebApplication app, Dictionary<string, string> headers)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(RoutePath, UriKind.Relative));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>The destination: records the headers of every request it receives and answers 200.</summary>
    private sealed class RecordingDestination : IForwarderHttpClientFactory
    {
        private readonly ConcurrentQueue<Dictionary<string, string>> _requests = new();

        public IReadOnlyList<Dictionary<string, string>> Requests => [.. _requests];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new RecordingHandler(_requests), disposeHandler: true);

        private sealed class RecordingHandler(ConcurrentQueue<Dictionary<string, string>> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsolutePath == RoutePath)
                {
                    requests.Enqueue(request.Headers.ToDictionary(
                        header => header.Key,
                        header => string.Join(',', header.Value),
                        StringComparer.OrdinalIgnoreCase));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
            }
        }
    }
}
