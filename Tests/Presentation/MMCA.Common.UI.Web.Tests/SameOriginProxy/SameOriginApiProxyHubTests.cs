using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Web.SameOriginProxy;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// SignalR through the proxy: the negotiate POST (TestServer) and a real WebSocket upgrade (two
/// loopback Kestrel servers, since an upgrade needs a real connection) both reach the hub carrying the
/// bearer the proxy took from the HttpOnly cookie, with nothing token-shaped sent by the client. The
/// upgrade carries the page's own <c>Origin</c>, as a browser always does; one without it is refused.
/// Over HTTP/2 a browser opens the socket with an RFC 8441 extended CONNECT (<c>:protocol websocket</c>,
/// no <c>Upgrade</c> header), which the proxy treats exactly like the HTTP/1.1 GET upgrade and forwards
/// to the HTTP/1.1 gateway as one.
/// </summary>
public sealed class SameOriginApiProxyHubTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Negotiate_GetsTheBearerServerSide()
    {
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();
        using var request = ProxyHost.Request(HttpMethod.Post, "/api/hubs/notifications/negotiate?negotiateVersion=1", access, "refresh-1", csrf: true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = _gateway.Seen.Should().ContainSingle().Subject;
        seen.PathAndQuery.Should().Be("/hubs/notifications/negotiate?negotiateVersion=1");
        seen.Authorization.Should().Be($"Bearer {access}");
    }

    [Fact]
    public async Task WebSocketUpgrade_FromTheOwnOrigin_IsForwarded_WithTheBearerAttachedServerSide()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));

        await using var upstream = await StartEchoHubAsync();
        await using var proxy = await StartProxyAsync(Address(upstream));

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={access}; {SessionCookieEndpoints.RefreshTokenCookieName}=refresh-1");
        socket.Options.SetRequestHeader("Origin", Address(proxy));
        await socket.ConnectAsync(HubUri(proxy), cancellationToken);

        var seenUpstream = await ReceiveSeenAsync(socket, cancellationToken);

        seenUpstream[0].Should().Be("/hubs/notifications");
        seenUpstream[1].Should().Be($"Bearer {access}", "the upgrade carries the bearer the proxy attached");
        seenUpstream[2].Should().NotContain(SessionCookieEndpoints.AccessTokenCookieName);
        seenUpstream[3].Should().Be("127.0.0.1", "the gateway still sees the caller for its per-IP rate limiter");
    }

    [Fact]
    public async Task WebSocketUpgrade_FromAnotherOrigin_IsRefused_AndNeverReachesTheHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var upstreamHits = 0;

        await using var upstream = await StartCountingUpstreamAsync(() => Interlocked.Increment(ref upstreamHits));
        await using var proxy = await StartProxyAsync(Address(upstream));

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={Jwt.Create(DateTime.UtcNow.AddMinutes(10))}");
        socket.Options.SetRequestHeader("Origin", "https://evil.example.com");
        Func<Task> connect = () => socket.ConnectAsync(HubUri(proxy), cancellationToken);

        (await connect.Should().ThrowAsync<WebSocketException>()).Which.Message.Should().Contain("403");
        Volatile.Read(ref upstreamHits).Should().Be(0);
    }

    [Fact]
    public async Task WebSocketOverHttp2_FromTheOwnOrigin_IsForwarded_AsAnHttp11Upgrade_WithTheBearerAttachedServerSide()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var access = Jwt.Create(DateTime.UtcNow.AddMinutes(10));

        await using var upstream = await StartEchoHubAsync();
        await using var proxy = await StartProxyAsync(Address(upstream), HttpProtocols.Http2);

        using var invoker = new HttpMessageInvoker(new SocketsHttpHandler());
        using var socket = Http2Socket();
        socket.Options.SetRequestHeader("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={access}; {SessionCookieEndpoints.RefreshTokenCookieName}=refresh-1");
        socket.Options.SetRequestHeader("Origin", Address(proxy));
        socket.Options.SetRequestHeader("Sec-Fetch-Site", "same-origin");
        await socket.ConnectAsync(HubUri(proxy), invoker, cancellationToken);

        socket.HttpStatusCode.Should().Be(HttpStatusCode.OK, "an HTTP/2 WebSocket is accepted with 200, not 101");
        var seenUpstream = await ReceiveSeenAsync(socket, cancellationToken);

        seenUpstream[0].Should().Be("/hubs/notifications");
        seenUpstream[1].Should().Be($"Bearer {access}", "the extended CONNECT carries the bearer the proxy attached");
        seenUpstream[2].Should().NotContain(SessionCookieEndpoints.AccessTokenCookieName);
        seenUpstream[4].Should().Be("HTTP/1.1", "the gateway hop stays HTTP/1.1, so the extended CONNECT is forwarded as a GET upgrade");
    }

    [Fact]
    public async Task WebSocketOverHttp2_FromAnotherOrigin_IsRefused_AndNeverReachesTheHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var upstreamHits = 0;

        await using var upstream = await StartCountingUpstreamAsync(() => Interlocked.Increment(ref upstreamHits));
        await using var proxy = await StartProxyAsync(Address(upstream), HttpProtocols.Http2);

        using var invoker = new HttpMessageInvoker(new SocketsHttpHandler());
        using var socket = Http2Socket();
        socket.Options.SetRequestHeader("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={Jwt.Create(DateTime.UtcNow.AddMinutes(10))}");
        socket.Options.SetRequestHeader("Origin", "https://evil.example.com");
        Func<Task> connect = () => socket.ConnectAsync(HubUri(proxy), invoker, cancellationToken);

        await connect.Should().ThrowAsync<WebSocketException>();
        socket.HttpStatusCode.Should().Be(HttpStatusCode.Forbidden);
        Volatile.Read(ref upstreamHits).Should().Be(0);
    }

    [Fact]
    public async Task WebSocketOverHttp2_WithoutOrigin_IsRefused_AsCrossOrigin_AndNeverReachesTheHub()
    {
        var upstreamHits = 0;

        await using var upstream = await StartCountingUpstreamAsync(() => Interlocked.Increment(ref upstreamHits));
        await using var proxy = await StartProxyAsync(Address(upstream), HttpProtocols.Http2);

        var (status, body) = await SendExtendedConnectAsync(proxy, "websocket", origin: null);

        status.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("cross_origin_rejected", "an HTTP/2 WebSocket needs the page's own Origin exactly as an HTTP/1.1 upgrade does");
        Volatile.Read(ref upstreamHits).Should().Be(0);
    }

    [Fact]
    public async Task ExtendedConnect_ForAnotherProtocol_StillNeedsTheCsrfHeader_AndNeverReachesTheGateway()
    {
        var upstreamHits = 0;

        await using var upstream = await StartCountingUpstreamAsync(() => Interlocked.Increment(ref upstreamHits));
        await using var proxy = await StartProxyAsync(Address(upstream), HttpProtocols.Http2);

        var (status, body) = await SendExtendedConnectAsync(proxy, "not-websocket", origin: Address(proxy));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("csrf_header_required", "only a WebSocket is exempt from the CSRF header, not every CONNECT");
        Volatile.Read(ref upstreamHits).Should().Be(0);
    }

    // A raw RFC 8441 extended CONNECT, so the test can read the proxy's error body.
    private static async Task<(HttpStatusCode Status, string Body)> SendExtendedConnectAsync(WebApplication proxy, string protocol, string? origin)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new SocketsHttpHandler();
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Connect, Address(proxy) + "/api/hubs/notifications")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Protocol = protocol;
        request.Headers.Add("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={Jwt.Create(DateTime.UtcNow.AddMinutes(10))}");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static Uri HubUri(WebApplication proxy) =>
        new(Address(proxy).Replace("http://", "ws://", StringComparison.Ordinal) + "/api/hubs/notifications");

    // Cleartext HTTP/2 with prior knowledge: the extended CONNECT a browser sends over h2, minus the certificate.
    private static ClientWebSocket Http2Socket()
    {
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version20;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.CollectHttpResponseDetails = true;
        return socket;
    }

    private static async Task<string[]> ReceiveSeenAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, cancellationToken);
        var seen = Encoding.UTF8.GetString(buffer, 0, received.Count).Split('|');

        // Complete the close handshake the hub started, so neither side logs an aborted connection.
        if ((await socket.ReceiveAsync(buffer, cancellationToken)).MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
        }

        return seen;
    }

    /// <summary>An HTTP/1.1 stand-in hub: accepts the socket, sends back what it saw, closes.</summary>
    private static Task<WebApplication> StartEchoHubAsync() => StartKestrelAsync(
        _ => { },
        app =>
        {
            app.UseWebSockets();
            app.Run(async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var seen = $"{context.Request.Path}|{context.Request.Headers.Authorization}|{context.Request.Headers.Cookie}|{context.Request.Headers["X-Forwarded-For"]}|{context.Request.Protocol}";
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(Encoding.UTF8.GetBytes(seen), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            });
        });

    private static Task<WebApplication> StartCountingUpstreamAsync(Action onHit) => StartKestrelAsync(
        _ => { },
        app => app.Run(context =>
        {
            onHit();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }));

    private static Task<WebApplication> StartProxyAsync(string upstreamAddress, HttpProtocols protocols = HttpProtocols.Http1) => StartKestrelAsync(
        builder =>
        {
            builder.Configuration["Api:ApiEndpoint"] = upstreamAddress;
            builder.Services.AddRouting();
            builder.Services.AddServerAuthSessionCookie(upstreamAddress);
            builder.Services.AddCommonSameOriginApiProxy(builder.Configuration);
        },
        app => app.MapCommonSameOriginApiProxy(),
        protocols);

    private static async Task<WebApplication> StartKestrelAsync(
        Action<WebApplicationBuilder> configureBuilder,
        Action<WebApplication> configureApp,
        HttpProtocols protocols = HttpProtocols.Http1)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.WebHost.UseKestrel(kestrel => kestrel.ConfigureEndpointDefaults(listen => listen.Protocols = protocols)).UseUrls("http://127.0.0.1:0");
        configureBuilder(builder);
        var app = builder.Build();
        configureApp(app);
        await app.StartAsync();
        return app;
    }
}
