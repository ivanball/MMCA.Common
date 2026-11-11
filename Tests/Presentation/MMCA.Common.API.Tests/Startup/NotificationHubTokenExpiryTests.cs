using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// In-memory host tests for <see cref="PushNotificationSettings.CloseConnectionsOnTokenExpiry"/> as
/// honoured by <c>MapNotificationHub()</c>: the real <c>NotificationHub</c>, registered through
/// <c>AddPushNotifications</c>, is reached by a real SignalR client over the test server's WebSocket
/// transport, authenticated by a test scheme whose ticket expires a couple of seconds after it is issued.
/// With the setting on, the server ends the connection once that ticket expires, a client that can still
/// authenticate reconnects, and a client whose authentication is now refused stays disconnected. With the
/// setting off (the default) the connection outlives its ticket.
/// </summary>
public sealed class NotificationHubTokenExpiryTests
{
    private const string HubPath = "/hubs/notifications";
    private const string CloseOnExpiryKey = "PushNotifications:CloseConnectionsOnTokenExpiry";

    /// <summary>How long each ticket the test scheme issues stays valid.</summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(2);

    /// <summary>How long, from the start of the connection, the server has to end it.</summary>
    private static readonly TimeSpan LeaveDeadline = TicketLifetime + TimeSpan.FromSeconds(6);

    /// <summary>How long a follow-up event (reconnected, closed) has after the connection was ended.</summary>
    private static readonly TimeSpan FollowUpDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Slack for clock granularity when comparing the close instant with the ticket expiry.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.Zero, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)];

    [Fact]
    public void CloseConnectionsOnTokenExpiry_IsOffByDefault()
    {
        new PushNotificationSettings().CloseConnectionsOnTokenExpiry.Should().BeFalse();

        BindSettings(new Dictionary<string, string?> { ["PushNotifications:Enabled"] = "true" })
            .CloseConnectionsOnTokenExpiry.Should().BeFalse("an unset key keeps the setting off");
    }

    [Fact]
    public void CloseConnectionsOnTokenExpiry_BindsFromThePushNotificationsSection() =>
        BindSettings(new Dictionary<string, string?> { [CloseOnExpiryKey] = "true" })
            .CloseConnectionsOnTokenExpiry.Should().BeTrue();

    [Fact]
    public async Task MapNotificationHub_WithCloseOnTokenExpiryOn_ServerClosesTheConnectionOnceTheTicketExpires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var issuer = new TicketIssuer();
        await using var app = await StartHubAsync(issuer, closeOnTokenExpiry: true);
        await using var connection = CreateConnection(app, automaticReconnect: false);
        var observer = ConnectionObserver.Attach(connection);

        await connection.StartAsync(cancellationToken);
        connection.State.Should().Be(HubConnectionState.Connected);
        var ticketExpiry = issuer.LastExpiry;

        (await CompletesWithinAsync(observer.Closed.Task, LeaveDeadline, cancellationToken))
            .Should().BeTrue("the server must close the connection once the ticket it was authenticated with expires");
        (await observer.LeftConnected.Task).Should().BeOnOrAfter(
            ticketExpiry - ClockTolerance,
            "the connection is ended by the ticket expiring, not before it");
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task MapNotificationHub_WithCloseOnTokenExpiryOff_KeepsTheConnectionOpenPastTheTicketExpiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var issuer = new TicketIssuer();
        await using var app = await StartHubAsync(issuer, closeOnTokenExpiry: null);
        await using var connection = CreateConnection(app, automaticReconnect: false);
        var observer = ConnectionObserver.Attach(connection);

        await connection.StartAsync(cancellationToken);
        connection.State.Should().Be(HubConnectionState.Connected);

        // Asserting that nothing happens, so a fixed wait: twice the ticket lifetime plus a heartbeat.
        await Task.Delay(TicketLifetime * 2 + TimeSpan.FromSeconds(1), cancellationToken);

        observer.Closed.Task.IsCompleted.Should().BeFalse("with the setting off the connection outlives its ticket");
        observer.LeftConnected.Task.IsCompleted.Should().BeFalse();
        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task MapNotificationHub_WithCloseOnTokenExpiryOn_AutomaticReconnectRestoresTheConnection_WhileTheUserCanAuthenticate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var issuer = new TicketIssuer();
        await using var app = await StartHubAsync(issuer, closeOnTokenExpiry: true);
        await using var connection = CreateConnection(app, automaticReconnect: true);
        var observer = ConnectionObserver.Attach(connection);

        await connection.StartAsync(cancellationToken);
        connection.State.Should().Be(HubConnectionState.Connected);

        (await CompletesWithinAsync(observer.LeftConnected.Task, LeaveDeadline, cancellationToken))
            .Should().BeTrue("the server must end the connection once its ticket expires");
        (await CompletesWithinAsync(observer.Reconnected.Task, FollowUpDeadline, cancellationToken))
            .Should().BeTrue("a user who can still authenticate gets a fresh connection with a fresh ticket");
        connection.State.Should().Be(HubConnectionState.Connected);
        observer.Closed.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task MapNotificationHub_WithCloseOnTokenExpiryOn_DoesNotReconnect_OnceAuthenticationIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var issuer = new TicketIssuer();
        await using var app = await StartHubAsync(issuer, closeOnTokenExpiry: true);
        await using var connection = CreateConnection(app, automaticReconnect: true);
        var observer = ConnectionObserver.Attach(connection);

        await connection.StartAsync(cancellationToken);
        connection.State.Should().Be(HubConnectionState.Connected);

        // The user is locked or their sessions revoked: from here on no request authenticates.
        issuer.Refuse();

        (await CompletesWithinAsync(observer.LeftConnected.Task, LeaveDeadline, cancellationToken))
            .Should().BeTrue("the server must end the connection once its ticket expires, even though nothing else changed on it");
        (await CompletesWithinAsync(observer.Closed.Task, FollowUpDeadline, cancellationToken))
            .Should().BeTrue("every reconnect attempt is refused, so the client gives up");
        observer.Reconnected.Task.IsCompleted.Should().BeFalse("a refused user must not get the connection back");
        connection.State.Should().NotBe(HubConnectionState.Connected);
    }

    private static PushNotificationSettings BindSettings(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddPushNotifications(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PushNotificationSettings>>().Value;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The real hub mapped by <c>MapNotificationHub()</c>, behind the expiring-ticket test scheme. A null
    /// <paramref name="closeOnTokenExpiry"/> leaves the key unset so the default applies.
    /// </summary>
    private static async Task<WebApplication> StartHubAsync(TicketIssuer issuer, bool? closeOnTokenExpiry)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();

        // No log providers: a connection torn down with the host otherwise logs into a disposed logger.
        builder.Logging.ClearProviders();

        var settings = new Dictionary<string, string?> { ["PushNotifications:Enabled"] = "true" };
        if (closeOnTokenExpiry is { } value)
        {
            settings[CloseOnExpiryKey] = value ? "true" : "false";
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton(issuer);
        builder.Services
            .AddAuthentication(ExpiringTicketHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ExpiringTicketHandler>(ExpiringTicketHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddPushNotifications(builder.Configuration);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNotificationHub();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>A SignalR client on the WebSocket transport, carried over the in-memory test server.</summary>
    private static HubConnection CreateConnection(WebApplication app, bool automaticReconnect)
    {
        var server = app.GetTestServer();
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPath.TrimStart('/')), options =>
            {
                options.Transports = HttpTransportType.WebSockets;

                // As the shipped NotificationHubService client does. It also makes each connection
                // attempt ONE authenticated request: with negotiation there are two (negotiate, then
                // the WebSocket), each issued its own ticket, so TicketIssuer.LastExpiry named a later
                // ticket than the one the server had bound to the connection and the lower-bound
                // assertion failed whenever the two requests landed far enough apart.
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = (context, cancellationToken) => ConnectWebSocketAsync(server, context.Uri, cancellationToken);
            });

        if (automaticReconnect)
        {
            builder.WithAutomaticReconnect(ReconnectDelays);
        }

        return builder.Build();
    }

    private static async ValueTask<WebSocket> ConnectWebSocketAsync(TestServer server, Uri uri, CancellationToken cancellationToken) =>
        await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);

    /// <summary>Records the client-side lifecycle events of one connection.</summary>
    private sealed class ConnectionObserver
    {
        /// <summary>Completes, with the instant, the first time the connection leaves Connected.</summary>
        public TaskCompletionSource<DateTimeOffset> LeftConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static ConnectionObserver Attach(HubConnection connection)
        {
            var observer = new ConnectionObserver();
            connection.Closed += _ =>
            {
                observer.LeftConnected.TrySetResult(DateTimeOffset.UtcNow);
                observer.Closed.TrySetResult();
                return Task.CompletedTask;
            };
            connection.Reconnecting += _ =>
            {
                observer.LeftConnected.TrySetResult(DateTimeOffset.UtcNow);
                return Task.CompletedTask;
            };
            connection.Reconnected += _ =>
            {
                observer.Reconnected.TrySetResult();
                return Task.CompletedTask;
            };
            return observer;
        }
    }

    /// <summary>
    /// Issues a fresh ticket per request for one user, each expiring <see cref="TicketLifetime"/> after it
    /// is issued, until <see cref="Refuse"/> is called; from then on no request authenticates.
    /// </summary>
    private sealed class TicketIssuer
    {
        private readonly Lock _gate = new();
        private readonly string _userId = Guid.NewGuid().ToString("N");
        private bool _refuse;
        private DateTimeOffset _lastExpiry;

        /// <summary>Gets the expiry of the most recently issued ticket.</summary>
        public DateTimeOffset LastExpiry
        {
            get
            {
                lock (_gate)
                {
                    return _lastExpiry;
                }
            }
        }

        public void Refuse()
        {
            lock (_gate)
            {
                _refuse = true;
            }
        }

        public AuthenticateResult Authenticate(string scheme)
        {
            lock (_gate)
            {
                if (_refuse)
                {
                    return AuthenticateResult.NoResult();
                }

                var issued = DateTimeOffset.UtcNow;
                _lastExpiry = issued + TicketLifetime;
                var identity = new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, _userId)], scheme);
                var properties = new AuthenticationProperties { IssuedUtc = issued, ExpiresUtc = _lastExpiry };
                return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, scheme));
            }
        }
    }

    /// <summary>Authenticates every request through the shared <see cref="TicketIssuer"/>.</summary>
    private sealed class ExpiringTicketHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TicketIssuer issuer) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ExpiringTicket";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(issuer.Authenticate(Scheme.Name));
    }
}
