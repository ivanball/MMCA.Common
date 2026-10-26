using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// L240: the cached probe endpoints answer "exactly as MapHealthChecks does", which includes the three
/// no-cache headers the stock middleware writes. They are asserted against fixed values and against
/// what the stock <c>/alive</c> endpoint returns from the same host, so the parity the doc promises
/// is the thing pinned.
/// </summary>
public sealed class CachedHealthEndpointHeadersTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task CachedHealth_WritesTheSameNoCacheHeadersAsTheStockEndpoint(string method)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddDefaultHealthChecks();
        await using var app = builder.Build();
        app.MapCachedHealthChecks(HealthEndpointPaths.Health, predicate: null);
        app.MapHealthChecks(HealthEndpointPaths.Alive);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var health = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), HealthEndpointPaths.Health),
            TestContext.Current.CancellationToken);
        using var alive = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), HealthEndpointPaths.Alive),
            TestContext.Current.CancellationToken);

        health.Headers.CacheControl!.ToString().Should().Be("no-store, no-cache");
        health.Headers.Pragma.ToString().Should().Be("no-cache");
        health.Content.Headers.Expires.Should().Be(DateTimeOffset.UnixEpoch);

        health.Headers.CacheControl.ToString().Should().Be(alive.Headers.CacheControl!.ToString());
        health.Headers.Pragma.ToString().Should().Be(alive.Headers.Pragma.ToString());
        health.Content.Headers.Expires.Should().Be(alive.Content.Headers.Expires);
    }
}
