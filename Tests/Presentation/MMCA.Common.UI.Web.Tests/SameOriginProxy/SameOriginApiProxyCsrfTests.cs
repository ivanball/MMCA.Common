using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The proxy's CSRF gate: every unsafe method must carry <c>X-CSRF: 1</c> (a header a cross-site page
/// cannot add without a preflight the proxy never grants) or it is refused with 403 before anything
/// reaches the gateway; safe methods need no header.
/// </summary>
public sealed class SameOriginApiProxyCsrfTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    public static TheoryData<string> UnsafeMethods => ["POST", "PUT", "PATCH", "DELETE"];

    [Theory]
    [MemberData(nameof(UnsafeMethods))]
    public async Task UnsafeMethod_WithoutTheHeader_Is403_AndNeverForwarded(string method)
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(new HttpMethod(method), "/api/orders/5", Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _gateway.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task UnsafeMethod_WithAWrongHeaderValue_Is403()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/orders");
        request.Headers.Add("X-CSRF", "true");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(UnsafeMethods))]
    public async Task UnsafeMethod_WithTheHeader_IsForwarded_WithoutTheHeaderUpstream(string method)
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(new HttpMethod(method), "/api/orders/5", Jwt.Create(DateTime.UtcNow.AddMinutes(10)), "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = _gateway.Seen.Should().ContainSingle().Subject;
        seen.Method.Should().Be(method);
        seen.Csrf.Should().BeNull("the header is a same-origin proof for the proxy, not something the gateway needs");
    }

    [Fact]
    public async Task Get_WithoutTheHeader_IsForwarded()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Get, "/api/orders");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle();
    }
}
