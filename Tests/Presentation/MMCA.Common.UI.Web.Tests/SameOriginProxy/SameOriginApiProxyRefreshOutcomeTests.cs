using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// What the proxy does when a refresh produces no token, on each of its four refresh call paths (an
/// expired access cookie, a refresh cookie alone, the forced refresh after an upstream 401, and the
/// locally answered <c>POST {prefix}/auth/refresh</c>): only a refresh the identity endpoint refused
/// ends the session (401, both cookies cleared); a refresh it could not decide (5xx, 429) keeps the
/// cookies, forwards and replays nothing, and answers 503 with <c>Retry-After</c>.
/// </summary>
public sealed class SameOriginApiProxyRefreshOutcomeTests : IAsyncLifetime
{
    private const string ExpiredAccessCookie = "expired-access-cookie";
    private const string RefreshCookieOnly = "refresh-cookie-only";
    private const string Upstream401Replay = "upstream-401-replay";
    private const string LocalRefreshEndpoint = "local-refresh-endpoint";

    private FakeGateway _gateway = null!;

    public static TheoryData<string> CallPaths =>
        [ExpiredAccessCookie, RefreshCookieOnly, Upstream401Replay, LocalRefreshEndpoint];

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Theory]
    [MemberData(nameof(CallPaths))]
    public async Task RefreshUnavailable_Answers503WithTheUpstreamRetryAfter_AndKeepsTheCookies(string path)
    {
        _gateway.RefreshFailureStatus = HttpStatusCode.ServiceUnavailable;
        _gateway.RefreshRetryAfter = "17";
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = CreateRequest(path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "a blip at the identity endpoint is not a sign-out");
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(17));
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("session_refresh_unavailable");
        ProxyHost.SetCookies(response).Should().BeEmpty("the session cookies must survive a transient refresh failure");
        _gateway.RefreshCalls.Should().Be(1);
        _gateway.Seen.Should().HaveCount(string.Equals(path, Upstream401Replay, StringComparison.Ordinal) ? 1 : 0, "nothing is forwarded or replayed without a token");
    }

    [Fact]
    public async Task RefreshThrottled_WithoutRetryAfter_Answers503WithTheDefaultRetryAfter_AndKeepsTheCookies()
    {
        _gateway.RefreshFailureStatus = HttpStatusCode.TooManyRequests;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = CreateRequest(ExpiredAccessCookie);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        ProxyHost.SetCookies(response).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(CallPaths))]
    public async Task RefreshRejected_Answers401_AndClearsBothCookies(string path)
    {
        _gateway.RefreshFailureStatus = HttpStatusCode.Unauthorized;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = CreateRequest(path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("session_expired");
        ProxyHost.SetCookies(response).Should().HaveCount(2).And.OnlyContain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        _gateway.Seen.Should().HaveCount(string.Equals(path, Upstream401Replay, StringComparison.Ordinal) ? 1 : 0, "a dead session is never replayed");
    }

    [Fact]
    public async Task RefreshRejectedWithBadRequest_Answers401_AndClearsBothCookies()
    {
        _gateway.RefreshFailureStatus = HttpStatusCode.BadRequest;
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = CreateRequest(ExpiredAccessCookie);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ProxyHost.SetCookies(response).Should().HaveCount(2);
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var expired = Jwt.Create(DateTime.UtcNow.AddMinutes(-5));
        switch (path)
        {
            case ExpiredAccessCookie:
                return ProxyHost.Request(HttpMethod.Get, "/api/orders", expired, "refresh-1");
            case RefreshCookieOnly:
                return ProxyHost.Request(HttpMethod.Get, "/api/orders", accessToken: null, refreshToken: "refresh-1");
            case Upstream401Replay:
                var revoked = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
                _gateway.RejectedTokens[revoked] = true;
                return ProxyHost.Request(HttpMethod.Get, "/api/orders", revoked, "refresh-1");
            default:
                return ProxyHost.Request(HttpMethod.Post, "/api/auth/refresh", expired, "refresh-1", csrf: true);
        }
    }
}
