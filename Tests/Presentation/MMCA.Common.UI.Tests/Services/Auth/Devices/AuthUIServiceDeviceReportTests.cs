using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Devices;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;
using TestContext = Xunit.TestContext;

namespace MMCA.Common.UI.Tests.Services.Auth.Devices;

/// <summary>
/// The per-session device report (<see cref="IDeviceMetadataReporter"/>) as <see cref="AuthUIService"/>
/// drives it after a sign-in: sent once the new session is stored and announced, and never able to fail
/// or hold up the sign-in. The refresh backfill is covered by <c>SecureStoreDeviceBackfillTests</c>.
/// </summary>
public sealed class AuthUIServiceDeviceReportTests : IDisposable
{
    private readonly Mock<ITokenStorageService> _tokenStorage = new();
    private readonly Mock<ITokenRefresher> _tokenRefresher = new();
    private readonly Mock<IDeviceMetadataReporter> _reporter = new();
    private readonly JwtAuthenticationStateProvider _authStateProvider;
    private readonly List<StubHttpMessageHandler> _handlers = [];

    private bool _tokensStored;
    private bool _authenticationAnnounced;

    public AuthUIServiceDeviceReportTests()
    {
        _tokenStorage
            .Setup(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => _tokensStored = true)
            .Returns(Task.CompletedTask);
        _authStateProvider = new JwtAuthenticationStateProvider(_tokenStorage.Object);
        _authStateProvider.AuthenticationStateChanged += _ => _authenticationAnnounced = true;
        _reporter.Setup(r => r.ReportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoginAsync_OnSuccess_ReportsTheDeviceOnce_AfterTheSessionIsStoredAndAnnounced()
    {
        var storedWhenReported = false;
        var announcedWhenReported = false;
        _reporter
            .Setup(r => r.ReportAsync(It.IsAny<CancellationToken>()))
            .Callback(() => (storedWhenReported, announcedWhenReported) = (_tokensStored, _authenticationAnnounced))
            .ReturnsAsync(true);
        var sut = CreateSut(_ => AuthResponse(Jwt()));

        var result = await sut.LoginAsync(new LoginRequest("user@test.com", "P@ssw0rd!"), Ct);

        result.IsSuccess.Should().BeTrue();
        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Once);
        storedWhenReported.Should().BeTrue("the report's bearer must name the session the sign-in just opened");
        announcedWhenReported.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_OnSuccess_ReportsTheDevice()
    {
        var sut = CreateSut(_ => AuthResponse(Jwt()));

        var result = await sut.RegisterAsync(new RegisterRequest("user@test.com", "P@ssw0rd!", "Test", "User"), Ct);

        result.IsSuccess.Should().BeTrue();
        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenTheSignInFails_ReportsNothing()
    {
        var sut = CreateSut(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.Unauthorized));

        var result = await sut.LoginAsync(new LoginRequest("user@test.com", "wrong"), Ct);

        result.IsFailure.Should().BeTrue();
        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenTheReporterThrows_StillSucceeds()
    {
        _reporter
            .Setup(r => r.ReportAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("device endpoint unreachable"));
        var sut = CreateSut(_ => AuthResponse(Jwt()));

        var result = await sut.LoginAsync(new LoginRequest("user@test.com", "P@ssw0rd!"), Ct);

        result.IsSuccess.Should().BeTrue("a device report is best-effort and must never fail the sign-in");
        _tokensStored.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_WhenTheReporterNeverCompletes_ReturnsWithinTheBound()
    {
        // A report that ignores its cancellation token and never finishes: the sign-in still returns.
        _reporter
            .Setup(r => r.ReportAsync(It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<bool>().Task);
        var sut = CreateSut(_ => AuthResponse(Jwt()));
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.LoginAsync(new LoginRequest("user@test.com", "P@ssw0rd!"), Ct);

        stopwatch.Stop();
        result.IsSuccess.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the wait on the report is bounded");
    }

    private AuthUIService CreateSut(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        _handlers.Add(handler);
        return new AuthUIService(
            new StubHttpClientFactory(handler),
            _tokenStorage.Object,
            _tokenRefresher.Object,
            _authStateProvider,
            Mock.Of<IPushRegistrationService>(),
            deviceMetadataReporter: _reporter.Object);
    }

    private static HttpResponseMessage AuthResponse(string accessToken) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new AuthenticationResponse(accessToken, "refresh-token", DateTime.UtcNow.AddMinutes(15))),
        };

    /// <summary>A parseable JWT: <c>NotifyUserAuthentication</c> reads the claims out of the real token.</summary>
    private static string Jwt()
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(new byte[32]), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "test",
            audience: "test",
            claims: [new Claim("sub", "1"), new Claim("email", "user@test.com")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
