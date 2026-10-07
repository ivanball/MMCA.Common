using System.Collections.Concurrent;
using System.Net;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// <see cref="NotificationHubService"/> against a real SignalR hub on loopback Kestrel whose
/// authentication refuses every request. The client skips negotiation, so the only request it makes is
/// the WebSocket upgrade, carrying the bearer from token storage; the refused handshake has to surface
/// as <see cref="HttpRequestException"/> with status 401 (a bare <c>WebSocketException</c> carries no
/// status), which is what lets the service and <c>UnboundedReconnectPolicy</c> classify it as a refused
/// authentication and stop instead of retrying a session that has ended.
/// </summary>
public sealed class NotificationHubAuthRefusalTests
{
    private const string AccessToken = "hub-access-token";

    [Fact]
    public async Task StartAsync_WhenTheHubRefusesTheWebSocketHandshake_StopsAtTheFirstAttempt_WithA401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seen = new ConcurrentQueue<string>();
        await using var hub = await StartRefusingHubAsync(seen);

        var tokens = new Mock<ITokenStorageService>();
        tokens.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync(AccessToken);
        var logger = new RefusalCapturingLogger();
        await using var sut = new NotificationHubService(
            tokens.Object,
            Options.Create(new ApiSettings { ApiEndpoint = Address(hub) }),
            logger);

        // A start that does not recognise the refusal walks its whole retry schedule (about 14s) instead.
        await sut.StartAsync().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        sut.IsConnected.Should().BeFalse();
        seen.Should().ContainSingle("the refused handshake ends the start at once, and no negotiate precedes it")
            .Which.Should().Be(
                $"GET /hubs/notifications upgrade=websocket auth=Bearer {AccessToken}",
                "the single request is the WebSocket upgrade itself, carrying the bearer from token storage");

        var refusal = logger.Refusals.Should().ContainSingle("the service classified the failure as a refused authentication").Subject;
        var httpFailure = FindHttpFailure(refusal);
        httpFailure.Should().NotBeNull("the refused handshake must surface as HttpRequestException, not a status-less WebSocketException");
        httpFailure!.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpRequestException? FindHttpFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http)
            {
                return http;
            }

            if (current is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Select(FindHttpFailure).FirstOrDefault(found => found is not null);
            }
        }

        return null;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>A minimal hub behind an authentication scheme that never authenticates anyone.</summary>
    private static async Task<WebApplication> StartRefusingHubAsync(ConcurrentQueue<string> seen)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        builder.Services
            .AddAuthentication(RefusingAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RefusingAuthenticationHandler>(RefusingAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            seen.Enqueue($"{context.Request.Method} {context.Request.Path} upgrade={context.Request.Headers.Upgrade} auth={context.Request.Headers.Authorization}");
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<ProbeHub>("/hubs/notifications").RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    private sealed class ProbeHub : Hub;

    /// <summary>Authenticates nobody, so the authorization policy on the hub challenges with 401.</summary>
    private sealed class RefusingAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Refusing";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>Keeps the exception of every "refused authentication" warning the service logs.</summary>
    private sealed class RefusalCapturingLogger : ILogger<NotificationHubService>
    {
        private readonly ConcurrentQueue<Exception> _refusals = new();

        public IReadOnlyCollection<Exception> Refusals => _refusals;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (exception is not null && formatter(state, exception).Contains("refused authentication", StringComparison.Ordinal))
            {
                _refusals.Enqueue(exception);
            }
        }
    }
}
