using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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

        await using var upstream = await StartKestrelAsync(
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

                    var seen = $"{context.Request.Path}|{context.Request.Headers.Authorization}|{context.Request.Headers.Cookie}|{context.Request.Headers["X-Forwarded-For"]}";
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    await socket.SendAsync(Encoding.UTF8.GetBytes(seen), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                });
            });
        var upstreamAddress = Address(upstream);

        await using var proxy = await StartKestrelAsync(
            builder =>
            {
                builder.Configuration["Api:ApiEndpoint"] = upstreamAddress;
                builder.Services.AddRouting();
                builder.Services.AddServerAuthSessionCookie(upstreamAddress);
                builder.Services.AddCommonSameOriginApiProxy(builder.Configuration);
            },
            app => app.MapCommonSameOriginApiProxy());

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={access}; {SessionCookieEndpoints.RefreshTokenCookieName}=refresh-1");
        socket.Options.SetRequestHeader("Origin", Address(proxy));
        await socket.ConnectAsync(new Uri(Address(proxy).Replace("http://", "ws://", StringComparison.Ordinal) + "/api/hubs/notifications"), cancellationToken);

        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, cancellationToken);
        var seenUpstream = Encoding.UTF8.GetString(buffer, 0, received.Count).Split('|');

        // Complete the close handshake the hub started, so neither side logs an aborted connection.
        if ((await socket.ReceiveAsync(buffer, cancellationToken)).MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
        }

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

        await using var upstream = await StartKestrelAsync(
            _ => { },
            app => app.Run(context =>
            {
                Interlocked.Increment(ref upstreamHits);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }));
        var upstreamAddress = Address(upstream);

        await using var proxy = await StartKestrelAsync(
            builder =>
            {
                builder.Configuration["Api:ApiEndpoint"] = upstreamAddress;
                builder.Services.AddRouting();
                builder.Services.AddServerAuthSessionCookie(upstreamAddress);
                builder.Services.AddCommonSameOriginApiProxy(builder.Configuration);
            },
            app => app.MapCommonSameOriginApiProxy());

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", $"{SessionCookieEndpoints.AccessTokenCookieName}={Jwt.Create(DateTime.UtcNow.AddMinutes(10))}");
        socket.Options.SetRequestHeader("Origin", "https://evil.example.com");
        Func<Task> connect = () => socket.ConnectAsync(
            new Uri(Address(proxy).Replace("http://", "ws://", StringComparison.Ordinal) + "/api/hubs/notifications"), cancellationToken);

        (await connect.Should().ThrowAsync<WebSocketException>()).Which.Message.Should().Contain("403");
        Volatile.Read(ref upstreamHits).Should().Be(0);
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static async Task<WebApplication> StartKestrelAsync(Action<WebApplicationBuilder> configureBuilder, Action<WebApplication> configureApp)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        configureBuilder(builder);
        var app = builder.Build();
        configureApp(app);
        await app.StartAsync();
        return app;
    }
}
