using System.Net.WebSockets;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.Fallback;
using Moq;

namespace MMCA.Common.API.Tests.Authorization;

/// <summary>
/// The fallback policy and SignalR hub invocations (Run 17 X-25). .NET 11 SignalR authorizes a hub
/// method whenever it carries any attribute, so the fallback policy runs per invocation with a
/// <see cref="HubInvocationContext"/> resource. Blazor's circuit hub lives under the exempt
/// <c>/_blazor</c> prefix, and a signed-out visitor's <c>ConnectCircuit</c> (the reconnect after a
/// connection drop) has to be authorized there, exactly as the connection itself was.
/// </summary>
public sealed class FallbackHubInvocationTests
{
    private const string ExemptHubPath = "/_blazor";

    [Fact]
    public async Task Handler_DeniesAnAnonymousHubInvocationOnAnOrdinaryPath()
    {
        var context = CreateInvocationContext("/hubs/private");

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("only an exempt prefix waives the authenticated-user rule");
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task Handler_GrantsAnAnonymousHubInvocationOnAnExemptPath()
    {
        var context = CreateInvocationContext(ExemptHubPath);

        await CreateHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(
            "an invocation is decided by the path the connection was admitted on, and /_blazor is exempt");
    }
#endif

    [Fact]
    public async Task Pipeline_AnonymousCallerInvokesAnAttributedHubMethodOnTheExemptPath()
    {
        await using var app = await StartHostAsync();
        await using var connection = CreateConnection(app);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        // The attribute is what makes .NET 11 run the fallback policy for this method, as the
        // compiler's NullableContextAttribute does for Blazor's ConnectCircuit.
        var reply = await connection.InvokeAsync<string>(
            nameof(CircuitLikeHub.ConnectCircuit), "circuit-1", TestContext.Current.CancellationToken);

        reply.Should().Be("reconnected:circuit-1");
    }

    private static FallbackAuthorizationHandler CreateHandler() =>
        new(Options.Create(new FallbackAuthorizationOptions()));

    private static AuthorizationHandlerContext CreateInvocationContext(string connectionPath)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = connectionPath;
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });

        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.Features).Returns(features);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        caller.SetupGet(c => c.User).Returns(user);

        var method = typeof(CircuitLikeHub).GetMethod(nameof(CircuitLikeHub.ConnectCircuit))!;
        var invocation = new HubInvocationContext(
            caller.Object,
            new ServiceCollection().BuildServiceProvider(),
            new CircuitLikeHub(),
            method,
            ["circuit-1"]);

        return new AuthorizationHandlerContext([new FallbackAuthorizationRequirement()], user, invocation);
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorizationPolicies();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<CircuitLikeHub>(ExemptHubPath);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HubConnection CreateConnection(WebApplication app)
    {
        var server = app.GetTestServer();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, ExemptHubPath.TrimStart('/')), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = (context, cancellationToken) => ConnectWebSocketAsync(server, context.Uri, cancellationToken);
            })
            .Build();
    }

    private static async ValueTask<WebSocket> ConnectWebSocketAsync(TestServer server, Uri uri, CancellationToken cancellationToken) =>
        await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);

    private sealed class HttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>A marker attribute: any attribute on a hub method sends .NET 11 down the authorization path.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CircuitMethodAttribute : Attribute;

    /// <summary>Stands in for Blazor's circuit hub: one attributed method, no authorization of its own.</summary>
    public sealed class CircuitLikeHub : Hub
    {
        [CircuitMethod]
        public string ConnectCircuit(string circuitId) => "reconnected:" + circuitId;
    }
}
