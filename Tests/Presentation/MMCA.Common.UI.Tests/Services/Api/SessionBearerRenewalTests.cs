using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using MMCA.Common.UI.Services.Notifications;
using MMCA.Common.UI.Services.Preferences;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// Store Run 5 R6a: every framework call that puts the session's bearer on a request gets the API's 401
/// handling, wherever it attaches the bearer from. Each call runs the way a browser host runs it: the
/// bearer comes from the storage the page holds (a real <see cref="WasmTokenStorageService"/> over a
/// refresher double), while the <see cref="AuthDelegatingHandler"/> in the client pipeline resolves a
/// DIFFERENT storage instance of its own. The API answers 401 to the session's token. Pinned per call
/// path: the 401 makes exactly one token renewal; a refused renewal ends the session (SessionEnded); a
/// successful renewal does not.
/// </summary>
public sealed class SessionBearerRenewalTests
{
    private const string UnreadCount = "7";

    public enum CallPath
    {
        PreferenceReader,
        PreferenceWriter,
        AuthUIServiceAccountCall,
        NotificationInboxUnreadCount,
        NotificationInboxPage,
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(CallPath.PreferenceReader)]
    [InlineData(CallPath.PreferenceWriter)]
    [InlineData(CallPath.AuthUIServiceAccountCall)]
    [InlineData(CallPath.NotificationInboxUnreadCount)]
    [InlineData(CallPath.NotificationInboxPage)]
    public async Task A401_ToTheSessionsBearer_RenewsOnce_AndTheRefusedRenewalEndsTheSession(CallPath path)
    {
        var session = await Session.StartAsync(renewalRefused: true);

        await CallAsync(path, session);

        session.Renewals.Should().Be(1, "a 401 earns exactly one renewal of the session that attached the bearer");
        session.EndedCount.Should().Be(1, "a refused renewal ends the session");
    }

    [Theory]
    [InlineData(CallPath.PreferenceReader)]
    [InlineData(CallPath.PreferenceWriter)]
    [InlineData(CallPath.AuthUIServiceAccountCall)]
    [InlineData(CallPath.NotificationInboxUnreadCount)]
    [InlineData(CallPath.NotificationInboxPage)]
    public async Task A401_ToTheSessionsBearer_RenewsOnce_AndASuccessfulRenewalKeepsTheSession(CallPath path)
    {
        var session = await Session.StartAsync(renewalRefused: false);

        await CallAsync(path, session);

        session.Renewals.Should().Be(1, "a 401 earns exactly one renewal, also when the read is replayed after it");
        session.EndedCount.Should().Be(0, "a successful renewal does not end the session");
    }

    // The replay is what keeps the inbox read alive after the renewal: it must carry the renewed token.
    [Fact]
    public async Task NotificationInboxRead_AfterASuccessfulRenewal_IsReplayedWithTheRenewedToken()
    {
        var session = await Session.StartAsync(renewalRefused: false);
        var inbox = new NotificationInboxService(session.Factory, session.Storage, new NoScope(), session.Refresher.Object);

        var count = await inbox.GetUnreadCountAsync(Ct);

        count.IsSuccess.Should().BeTrue();
        count.Value.Should().Be(7);
        session.Renewals.Should().Be(1);
    }

    [Fact]
    public async Task NotificationInboxRead_AfterARefusedRenewal_ReportsAFailure_NotAnEmptyInbox()
    {
        var session = await Session.StartAsync(renewalRefused: true);
        var inbox = new NotificationInboxService(session.Factory, session.Storage, new NoScope(), session.Refresher.Object);

        var count = await inbox.GetUnreadCountAsync(Ct);

        count.IsSuccess.Should().BeFalse();
        session.Renewals.Should().Be(1);
    }

    // The direct hub handshake (server-side Blazor and MAUI) carries the session's bearer on a WebSocket
    // upgrade outside the API client pipeline. A loopback listener answers that upgrade with a 401.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotificationHub_HandshakeRefusedWith401_RenewsOnce_AndOnlyARefusedRenewalEndsTheSession(bool renewalRefused)
    {
        var session = await Session.StartAsync(renewalRefused);
        using var hubServer = RefusingHubServer.Start(HttpStatusCode.Unauthorized);

        await using (var hub = new NotificationHubService(
            session.Storage,
            Options.Create(new ApiSettings { ApiEndpoint = hubServer.Endpoint }),
            NullLogger<NotificationHubService>.Instance))
        {
            await hub.StartAsync();
            hub.IsConnected.Should().BeFalse();
        }

        hubServer.AuthorizationHeaders.Should().Contain("Bearer " + session.Token, "the handshake carried the session's bearer");
        session.Renewals.Should().Be(1, "a refused handshake earns exactly one renewal");
        session.EndedCount.Should().Be(renewalRefused ? 1 : 0);
    }

    [Fact]
    public async Task NotificationHub_HandshakeRefusedWith403_RenewsNothing()
    {
        var session = await Session.StartAsync(renewalRefused: true);
        using var hubServer = RefusingHubServer.Start(HttpStatusCode.Forbidden);

        await using (var hub = new NotificationHubService(
            session.Storage,
            Options.Create(new ApiSettings { ApiEndpoint = hubServer.Endpoint }),
            NullLogger<NotificationHubService>.Instance))
        {
            await hub.StartAsync();
        }

        hubServer.AuthorizationHeaders.Should().NotBeEmpty();
        session.Renewals.Should().Be(0, "a 403 is a signed-in user who is not allowed, not a refused session");
        session.EndedCount.Should().Be(0);
    }

    private static async Task CallAsync(CallPath path, Session session)
    {
        switch (path)
        {
            case CallPath.PreferenceReader:
                _ = await new ApiUserPreferenceReader(session.Factory, session.Storage).GetAsync(Ct);
                break;
            case CallPath.PreferenceWriter:
                await new ApiUserPreferenceWriter(session.Factory, session.Storage).SaveAsync("en-US", "dark", Ct);
                break;
            case CallPath.AuthUIServiceAccountCall:
                _ = await new AuthUIService(
                    session.Factory,
                    session.Storage,
                    session.Refresher.Object,
                    new Mock<AuthenticationStateProvider>().Object,
                    new Mock<IPushRegistrationService>().Object).GetSessionsAsync(Ct);
                break;
            case CallPath.NotificationInboxUnreadCount:
                _ = await new NotificationInboxService(session.Factory, session.Storage, new NoScope(), session.Refresher.Object).GetUnreadCountAsync(Ct);
                break;
            case CallPath.NotificationInboxPage:
                _ = await new NotificationInboxService(session.Factory, session.Storage, new NoScope(), session.Refresher.Object).GetInboxAsync(cancellationToken: Ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    private sealed class NoScope : INotificationScopeProvider
    {
        public Task<string?> GetCurrentScopeKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// One signed-in session: the storage the page holds (with the refresher a renewal goes through), a
    /// separate storage for the handler's own scope, and the API client factory whose API refuses the
    /// session's token and accepts only the renewed one.
    /// </summary>
    private sealed class Session : IHttpClientFactory
    {
        private readonly string _renewedToken = CreateUniqueJwt();
        private readonly WasmTokenStorageService _handlerScopeStorage;
        private int _ended;

        private Session(bool renewalRefused)
        {
            Refresher
                .Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(renewalRefused ? TokenAcquisition.NoSession : TokenAcquisition.Acquired(_renewedToken));
            Refresher
                .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(renewalRefused ? null : _renewedToken);
            Storage = new WasmTokenStorageService(SucceedingCookieSync(), Refresher.Object);
            Storage.SessionEnded += (_, _) => Interlocked.Increment(ref _ended);

            var handlerScopeRefresher = new Mock<ISessionAwareTokenRefresher>();
            handlerScopeRefresher
                .Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(TokenAcquisition.NoSession);
            _handlerScopeStorage = new WasmTokenStorageService(SucceedingCookieSync(), handlerScopeRefresher.Object);
        }

        public Mock<ISessionAwareTokenRefresher> Refresher { get; } = new();

        public WasmTokenStorageService Storage { get; }

        public string Token { get; } = CreateUniqueJwt();

        public IHttpClientFactory Factory => this;

        /// <summary>Gets how many times the session's token was renewed (every refresher call is one).</summary>
        public int Renewals => Refresher.Invocations.Count;

        public int EndedCount => Volatile.Read(ref _ended);

        public static async Task<Session> StartAsync(bool renewalRefused)
        {
            var session = new Session(renewalRefused);
            await session.Storage.SetTokensAsync(session.Token, "refresh-token");
            return session;
        }

        public HttpClient CreateClient(string name) =>
            new(new AuthDelegatingHandler(_handlerScopeStorage) { InnerHandler = new StubHttpMessageHandler(Answer) })
            {
                BaseAddress = new Uri("https://api.test/"),
            };

        private static string CreateUniqueJwt() =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                claims: [new Claim("jti", Guid.NewGuid().ToString("N"))],
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddMinutes(10)));

        private static ISessionCookieSync SucceedingCookieSync()
        {
            var sync = new Mock<ISessionCookieSync>();
            sync.Setup(s => s.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            sync.Setup(s => s.ClearAsync()).ReturnsAsync(true);
            return sync.Object;
        }

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            if (!string.Equals(request.Headers.Authorization?.Parameter, _renewedToken, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return request.RequestUri!.AbsolutePath.EndsWith("/unread-count", StringComparison.Ordinal)
                ? StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, UnreadCount)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PagedCollectionResult<UserNotificationDTO>([], new PaginationMetadata(0, 20, 1))),
                };
        }
    }

    /// <summary>
    /// A loopback listener that answers every WebSocket upgrade with <c>statusCode</c> and records the
    /// Authorization header each upgrade carried. Nothing leaves the machine.
    /// </summary>
    private sealed class RefusingHubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _authorizationHeaders = [];
        private readonly Lock _sync = new();
        private readonly string _response;
        private readonly Task _loop;

        private RefusingHubServer(HttpStatusCode statusCode)
        {
            _response = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Endpoint = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _loop = AcceptLoopAsync();
        }

        public string Endpoint { get; }

        public IReadOnlyList<string> AuthorizationHeaders
        {
            get
            {
                lock (_sync)
                {
                    return [.. _authorizationHeaders];
                }
            }
        }

        public static RefusingHubServer Start(HttpStatusCode statusCode) => new(statusCode);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _ = _loop.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var request = await ReadRequestHeadAsync(stream);
                foreach (var line in request.Split("\r\n"))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_sync)
                        {
                            _authorizationHeaders.Add(line["Authorization:".Length..].Trim());
                        }
                    }
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(_response), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }

        private async Task<string> ReadRequestHeadAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var buffer = new byte[1024];
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return head.ToString();
        }
    }
}
