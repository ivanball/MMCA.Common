using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using MMCA.Common.Shared.Resilience;
using MMCA.Common.UI.Web.Hosting;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// Specifies the server-side <c>"APIClient"</c> pipeline the composed Blazor web host leaves: no
/// standard resilience handler (whose 30 s attempt timeout cut off large uploads streamed from the
/// browser over the circuit) and the one 90 s <c>HttpClient.Timeout</c> that WebAssembly and MAUI
/// already run with. Other factory clients keep the service-defaults handler.
/// </summary>
public sealed class ApiClientPipelineTests
{
    [Fact]
    public async Task ApiClient_HasNoResilienceHandler()
    {
        await using var app = BuildApp();
        var factory = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var handlers = HandlerChain(factory.CreateHandler("APIClient"));

        handlers.Should().NotContain(handler => handler is ResilienceHandler);
    }

    [Fact]
    public async Task ApiClient_KeepsTheTotalRequestTimeout()
    {
        await using var app = BuildApp();
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient("APIClient");

        client.Timeout.Should().Be(HttpResilienceDefaults.TotalRequestTimeout);
    }

    /// <summary>
    /// The control: the removal is scoped to the one client, and the chain walk does see a
    /// resilience handler where service defaults put one.
    /// </summary>
    [Fact]
    public async Task OtherFactoryClients_KeepTheServiceDefaultsResilienceHandler()
    {
        await using var app = BuildApp();
        var factory = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var handlers = HandlerChain(factory.CreateHandler("SomeOtherClient"));

        handlers.Should().Contain(handler => handler is ResilienceHandler);
    }

    private static Microsoft.AspNetCore.Builder.WebApplication BuildApp()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();
        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());
        builder.Services.AddHttpClient("SomeOtherClient");
        return builder.Build();
    }

    private static List<HttpMessageHandler> HandlerChain(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? current = handler; current is not null;
            current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current);
        }

        return chain;
    }
}
