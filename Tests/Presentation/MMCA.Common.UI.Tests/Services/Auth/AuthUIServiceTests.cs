#pragma warning disable VSTHRD002 // Synchronous wait on an already-completed task raised by the auth state provider

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="AuthUIService"/> now that every API call returns a <see cref="Result"/>
/// instead of a nullable payload plus a <c>LastError</c> property.
/// <para>
/// Three contracts are pinned here. First, the <b>failure travels with the call</b>: the server's own
/// code, message and <see cref="ErrorType"/> survive the round trip through
/// <see cref="MMCA.Common.Shared.Http.ProblemDetailsResultReader"/>, and a request that never reached
/// a server becomes a transport failure rather than an exception. Second, a <b>2xx is not
/// automatically a success</b>: tokens that cannot be persisted (SSR prerender) and a response
/// carrying no access token are both reported as failures with their own codes. Third, the
/// <b>sign-out path is unconditional</b>: local tokens are cleared and auth state is notified whatever
/// the server or the push unregistration did.
/// </para>
/// </summary>
public sealed class AuthUIServiceTests : IDisposable
{
    private const string StoredAccessToken = "stored-access-token";

    /// <summary>An MMCA error array: every field round-trips, including the <c>type</c>.</summary>
    private const string MmcaErrorBody = """{ "title": "Unauthorized", "status": 401, "errors": [ { "code": "Auth.InvalidCredentials", "message": "Email or password is incorrect.", "type": "Unauthorized" } ] }""";

    /// <summary>A plain ProblemDetails body: no machine-readable code, so one is synthesized.</summary>
    private const string PlainProblemBody = """{ "title": "Unauthorized", "status": 401, "detail": "Your session has ended." }""";

    /// <summary>The ASP.NET Core validation-dictionary shape.</summary>
    private const string ValidationProblemBody = """{ "title": "One or more validation errors occurred.", "status": 400, "errors": { "Email": [ "That email address is already registered." ] } }""";

    /// <summary>Exactly the camelCase wire shape the sessions endpoint emits.</summary>
    private const string SessionsBody = """[ { "sessionId": "11111111-1111-1111-1111-111111111111", "createdAt": "2026-08-01T09:30:00Z", "expiresAt": "2026-09-01T09:30:00Z", "ipAddress": "203.0.113.7", "userAgent": "Mozilla/5.0 (Windows NT 10.0) Chrome/126.0.0.0", "isCurrent": true }, { "sessionId": "22222222-2222-2222-2222-222222222222", "createdAt": "2026-08-02T11:00:00Z", "expiresAt": "2026-09-02T11:00:00Z", "ipAddress": null, "userAgent": null, "isCurrent": false } ]""";

    private readonly Mock<ITokenStorageService> _tokenStorage = new();
    private readonly Mock<ITokenRefresher> _tokenRefresher = new();
    private readonly Mock<IPushRegistrationService> _pushRegistration = new();
    private readonly JwtAuthenticationStateProvider _authStateProvider;
    private readonly List<AuthenticationState> _authStates = [];

    private StubHttpMessageHandler _handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NotFound);

    public AuthUIServiceTests()
    {
        _tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(StoredAccessToken);

        // The real provider, not a stub: the service only notifies when the registered
        // AuthenticationStateProvider IS a JwtAuthenticationStateProvider, so a double would pass a
        // test the app cannot.
        _authStateProvider = new JwtAuthenticationStateProvider(_tokenStorage.Object);
        _authStateProvider.AuthenticationStateChanged += task => _authStates.Add(task.GetAwaiter().GetResult());
    }

    public void Dispose() => _handler.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthUIService CreateSut(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _handler.Dispose();
        _handler = new StubHttpMessageHandler(responder);
        return new AuthUIService(
            new StubHttpClientFactory(_handler),
            _tokenStorage.Object,
            _tokenRefresher.Object,
            _authStateProvider,
            _pushRegistration.Object);
    }

    private AuthUIService CreateSut(HttpStatusCode statusCode, string? json = null) =>
        CreateSut(_ => StubHttpMessageHandler.CreateResponse(statusCode, json));

    private static HttpResponseMessage AuthResponse(string accessToken, string refreshToken = "refresh-token") =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new AuthenticationResponse(accessToken, refreshToken, DateTime.UtcNow.AddMinutes(15))),
        };

    /// <summary>A parseable JWT: <c>NotifyUserAuthentication</c> reads the claims out of the real token.</summary>
    private static string Jwt(string email = "user@test.com")
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(new byte[32]), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "test",
            audience: "test",
            // Written under the short JWT names: ReadJwtToken applies no inbound claim mapping, so
            // what is written here is exactly what the auth state carries.
            claims: [new Claim("sub", "1"), new Claim("email", email)],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static LoginRequest Credentials() => new("user@test.com", "P@ssw0rd!");

    private static RegisterRequest Registration() => new("user@test.com", "P@ssw0rd!", "Test", "User");

    // ==================== Login ====================
    [Fact]
    public async Task LoginAsync_PostsCredentialsToAuthLoginOnTheApiClient()
    {
        var sut = CreateSut(_ => AuthResponse(Jwt()));

        await sut.LoginAsync(Credentials(), Ct);

        _handler.CallCount.Should().Be(1);
        _handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/login");
        _handler.LastRequest.Body.Should().Contain("user@test.com");
    }

    [Fact]
    public async Task LoginAsync_OnSuccess_StoresTokensAndReturnsTheResponse()
    {
        var accessToken = Jwt();
        var sut = CreateSut(_ => AuthResponse(accessToken, "rotating-refresh"));

        var result = await sut.LoginAsync(Credentials(), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be(accessToken);
        result.Value.RefreshToken.Should().Be("rotating-refresh");
        _tokenStorage.Verify(s => s.SetTokensAsync(accessToken, "rotating-refresh"), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_OnSuccess_NotifiesAuthenticatedState()
    {
        var sut = CreateSut(_ => AuthResponse(Jwt("signed-in@test.com")));

        await sut.LoginAsync(Credentials(), Ct);

        _authStates.Should().ContainSingle();
        _authStates[0].User.Identity!.IsAuthenticated.Should().BeTrue();
        _authStates[0].User.FindFirst("email")!.Value.Should().Be("signed-in@test.com");
    }

    [Fact]
    public async Task LoginAsync_WhenApiReturnsMmcaErrorArray_CarriesTheServerCodeMessageAndType()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, MmcaErrorBody);

        var result = await sut.LoginAsync(Credentials(), Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Auth.InvalidCredentials");
        error.Message.Should().Be("Email or password is incorrect.");
        error.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task LoginAsync_WhenApiReturnsPlainProblemDetails_SynthesizesAStatusCodedError()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, PlainProblemBody);

        var result = await sut.LoginAsync(Credentials(), Ct);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.401");
        error.Message.Should().Be("Your session has ended.");
        error.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task LoginAsync_WhenApiReturnsANonJsonBody_StillTypesTheFailureFromTheStatus()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("<html><body>Denied</body></html>"),
        });

        var result = await sut.LoginAsync(Credentials(), Ct);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.401");
        error.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task LoginAsync_OnFailure_StoresNothingAndNotifiesNothing()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, MmcaErrorBody);

        await sut.LoginAsync(Credentials(), Ct);

        _tokenStorage.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _authStates.Should().BeEmpty();
    }

    [Fact]
    public async Task LoginAsync_WhenTokenStorageIsUnavailable_FailsWithTokenStorageUnavailableCode()
    {
        // The SSR-prerender shape: valid credentials, but nothing on this device can hold them.
        _tokenStorage
            .Setup(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("JS interop is not available"));
        var sut = CreateSut(_ => AuthResponse(Jwt()));

        var result = await sut.LoginAsync(Credentials(), Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(AuthUIService.TokenStorageUnavailableCode);
        error.Type.Should().Be(ErrorType.Unexpected);
        error.Source.Should().Be("JS interop is not available");
        _authStates.Should().BeEmpty();
    }

    [Fact]
    public async Task LoginAsync_When2xxCarriesNoAccessToken_FailsWithMissingAccessTokenCode()
    {
        var sut = CreateSut(_ => AuthResponse(string.Empty));

        var result = await sut.LoginAsync(Credentials(), Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Code.Should().Be(AuthUIService.MissingAccessTokenCode);
        _tokenStorage.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _authStates.Should().BeEmpty();
    }

    [Fact]
    public async Task LoginAsync_WhenTheRequestNeverReachesAServer_ReturnsATransportFailure()
    {
        var sut = CreateSut(_ => throw new HttpRequestException("Connection refused"));

        var result = await sut.LoginAsync(Credentials(), Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected);
    }

    [Fact]
    public async Task LoginAsync_WhenTheCallerCancels_StillThrows()
    {
        // A page owns its own cancellation; it must not come back as an error to render.
        var sut = CreateSut(_ => AuthResponse(Jwt()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.LoginAsync(Credentials(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ==================== Register ====================
    [Fact]
    public async Task RegisterAsync_PostsToAuthRegisterAndStoresTheReturnedPair()
    {
        var accessToken = Jwt();
        var sut = CreateSut(_ => AuthResponse(accessToken, "new-refresh"));

        var result = await sut.RegisterAsync(Registration(), Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/register");
        _handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        _tokenStorage.Verify(s => s.SetTokensAsync(accessToken, "new-refresh"), Times.Once);
        _authStates.Should().ContainSingle();
    }

    [Fact]
    public async Task RegisterAsync_WhenApiReturnsAValidationDictionary_CarriesOneErrorPerMessage()
    {
        var sut = CreateSut(HttpStatusCode.BadRequest, ValidationProblemBody);

        var result = await sut.RegisterAsync(Registration(), Ct);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Validation.Email");
        error.Target.Should().Be("Email");
        error.Message.Should().Be("That email address is already registered.");
        error.Type.Should().Be(ErrorType.Validation);
    }

    // ==================== OAuth exchange ====================
    [Fact]
    public async Task ExchangeOAuthCodeAsync_PostsTheCodeAndStoresTheReturnedPair()
    {
        var accessToken = Jwt();
        var sut = CreateSut(_ => AuthResponse(accessToken, "oauth-refresh"));

        var result = await sut.ExchangeOAuthCodeAsync("single-use-code", Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/oauth/exchange");
        _handler.LastRequest.Body.Should().Contain("single-use-code");
        _tokenStorage.Verify(s => s.SetTokensAsync(accessToken, "oauth-refresh"), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExchangeOAuthCodeAsync_WithNoCode_FailsWithoutCallingTheApi(string? code)
    {
        var sut = CreateSut(_ => AuthResponse(Jwt()));

        var result = await sut.ExchangeOAuthCodeAsync(code!, Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Auth.OAuth.MissingCode");
        error.Type.Should().Be(ErrorType.Validation);
        _handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExchangeOAuthCodeAsync_WhenTheCodeIsRejected_CarriesTheServerFailure()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, MmcaErrorBody);

        var result = await sut.ExchangeOAuthCodeAsync("expired-code", Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.Unauthorized);
        _authStates.Should().BeEmpty();
    }

    // ==================== Logout ====================
    [Fact]
    public async Task LogoutAsync_RevokesOnTheServerWithTheBearerTokenThenSignsOutLocally()
    {
        var sut = CreateSut(HttpStatusCode.NoContent);

        await sut.LogoutAsync();

        _handler.CallCount.Should().Be(1);
        _handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/revoke");
        _handler.LastRequest.Authorization!.Scheme.Should().Be("Bearer");
        _handler.LastRequest.Authorization.Parameter.Should().Be(StoredAccessToken);
        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Once);
        _authStates.Should().ContainSingle();
        _authStates[0].User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task LogoutAsync_UnregistersThisDeviceForPushBeforeClearingTokens()
    {
        var sut = CreateSut(HttpStatusCode.NoContent);

        await sut.LogoutAsync();

        _pushRegistration.Verify(p => p.UnregisterAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LogoutAsync_WhenPushUnregistrationThrows_StillSignsOut()
    {
        _pushRegistration
            .Setup(p => p.UnregisterAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no push token"));
        var sut = CreateSut(HttpStatusCode.NoContent);

        await sut.LogoutAsync();

        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Once);
        _authStates.Should().ContainSingle();
    }

    [Fact]
    public async Task LogoutAsync_WhenTheServerRevokeFails_StillSignsOutLocally()
    {
        var sut = CreateSut(HttpStatusCode.InternalServerError);

        await sut.LogoutAsync();

        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Once);
        _authStates.Should().ContainSingle();
        _authStates[0].User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task LogoutAsync_WhenTheServerRevokeThrows_StillSignsOutLocally()
    {
        // A user who asked to leave must never be kept signed in by a dropped connection.
        var sut = CreateSut(_ => throw new HttpRequestException("Connection reset"));

        await sut.LogoutAsync();

        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Once);
        _authStates.Should().ContainSingle();
    }

    [Fact]
    public async Task LogoutAsync_WithNoStoredAccessToken_SkipsTheServerRevoke()
    {
        _tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync((string?)null);
        var sut = CreateSut(HttpStatusCode.NoContent);

        await sut.LogoutAsync();

        _handler.CallCount.Should().Be(0);
        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Once);
        _authStates.Should().ContainSingle();
    }

    [Fact]
    public async Task LogoutAsync_WhenClearingTokensIsUnavailable_StillNotifiesLogout()
    {
        _tokenStorage.Setup(s => s.ClearTokensAsync()).ThrowsAsync(new InvalidOperationException("JS interop"));
        var sut = CreateSut(HttpStatusCode.NoContent);

        await sut.LogoutAsync();

        _authStates.Should().ContainSingle();
        _authStates[0].User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    // ==================== Refresh ====================
    [Fact]
    public async Task TryRefreshTokenAsync_WhenTheHostRefresherReturnsAToken_NotifiesAuthenticationAndReturnsTrue()
    {
        var refreshed = Jwt("refreshed@test.com");
        _tokenRefresher.Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);
        var sut = CreateSut(HttpStatusCode.NoContent);

        var refreshedOk = await sut.TryRefreshTokenAsync(Ct);

        refreshedOk.Should().BeTrue();
        _authStates.Should().ContainSingle();
        _authStates[0].User.Identity!.IsAuthenticated.Should().BeTrue();
        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Never);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_WhenTheSessionIsGone_ClearsTokensNotifiesLogoutAndReturnsFalse()
    {
        _tokenRefresher.Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var sut = CreateSut(HttpStatusCode.NoContent);

        var refreshedOk = await sut.TryRefreshTokenAsync(Ct);

        refreshedOk.Should().BeFalse();
        _tokenStorage.Verify(s => s.ClearTokensAsync(), Times.Once);
        _authStates.Should().ContainSingle();
        _authStates[0].User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    // ==================== Password ====================
    [Fact]
    public async Task ChangePasswordAsync_PutsToAuthPasswordWithTheBearerToken()
    {
        var sut = CreateSut(HttpStatusCode.NoContent);

        var result = await sut.ChangePasswordAsync("old-pw", "new-pw", Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/password");
        _handler.LastRequest.Authorization!.Parameter.Should().Be(StoredAccessToken);
        _handler.LastRequest.Body.Should().Contain("old-pw").And.Contain("new-pw");
    }

    [Fact]
    public async Task ChangePasswordAsync_WhenTheCurrentPasswordIsWrong_CarriesTheServerFailure()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, MmcaErrorBody);

        var result = await sut.ChangePasswordAsync("wrong", "new-pw", Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Code.Should().Be("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task RequestPasswordResetAsync_PostsAnonymouslyAndTreats202AsSuccess()
    {
        var sut = CreateSut(HttpStatusCode.Accepted);

        var result = await sut.RequestPasswordResetAsync("user@test.com", Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/forgot-password");
        // No bearer: a signed-in caller must not have the reset bound to their current session.
        _handler.LastRequest.Authorization.Should().BeNull();
        _handler.LastRequest.Body.Should().Contain("user@test.com");
    }

    [Fact]
    public async Task ResetPasswordAsync_PostsEmailTokenAndPassword()
    {
        var sut = CreateSut(HttpStatusCode.NoContent);

        var result = await sut.ResetPasswordAsync("user@test.com", "reset-token", "new-pw", Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/reset-password");
        _handler.LastRequest.Authorization.Should().BeNull();
        _handler.LastRequest.Body.Should().Contain("reset-token");
    }

    [Fact]
    public async Task ResetPasswordAsync_WhenTheTokenIsSpent_CarriesTheServerFailure()
    {
        var sut = CreateSut(HttpStatusCode.BadRequest, ValidationProblemBody);

        var result = await sut.ResetPasswordAsync("user@test.com", "spent", "new-pw", Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.Validation);
    }

    // ==================== Sessions ====================
    [Fact]
    public async Task GetSessionsAsync_GetsMySessionsWithTheBearerTokenAndReadsTheCamelCaseList()
    {
        var sut = CreateSut(HttpStatusCode.OK, SessionsBody);

        var result = await sut.GetSessionsAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/my-sessions");
        _handler.LastRequest.Authorization!.Parameter.Should().Be(StoredAccessToken);

        var sessions = result.Value!;
        sessions.Should().HaveCount(2);
        sessions[0].SessionId.Should().Be(new Guid(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11));
        sessions[0].CreatedAt.Should().Be(new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc));
        sessions[0].ExpiresAt.Should().Be(new DateTime(2026, 9, 1, 9, 30, 0, DateTimeKind.Utc));
        sessions[0].IpAddress.Should().Be("203.0.113.7");
        sessions[0].UserAgent.Should().Contain("Chrome/126.0.0.0");
        sessions[0].IsCurrent.Should().BeTrue();
        sessions[1].IpAddress.Should().BeNull();
        sessions[1].UserAgent.Should().BeNull();
        sessions[1].IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task GetSessionsAsync_WithNoLiveSessions_SucceedsWithAnEmptyList()
    {
        var sut = CreateSut(HttpStatusCode.OK, "[]");

        var result = await sut.GetSessionsAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionsAsync_When401_ReportsAnUnauthorizedFailure()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, PlainProblemBody);

        var result = await sut.GetSessionsAsync(Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task GetSessionsAsync_WhenTokenStorageIsUnavailable_CallsAnonymouslyAndRendersThe401()
    {
        // SSR prerender: no token can be read, so the API answers 401 and the page renders it like
        // any other failure rather than seeing an exception.
        _tokenStorage.Setup(s => s.GetAccessTokenAsync()).ThrowsAsync(new InvalidOperationException("JS interop"));
        var sut = CreateSut(HttpStatusCode.Unauthorized, PlainProblemBody);

        var result = await sut.GetSessionsAsync(Ct);

        result.IsFailure.Should().BeTrue();
        _handler.LastRequest.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task GetSessionsAsync_WhenTheRequestNeverReachesAServer_ReturnsATransportFailure()
    {
        var sut = CreateSut(_ => throw new HttpRequestException("DNS failure"));

        var result = await sut.GetSessionsAsync(Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
    }

    // ==================== Per-device revoke ====================
    [Fact]
    public async Task RevokeSessionAsync_PostsToTheSessionScopedRevokeWithNoBody()
    {
        var sessionId = new Guid(0x33333333, 0x3333, 0x3333, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33);
        var sut = CreateSut(HttpStatusCode.NoContent);

        await sut.RevokeSessionAsync(sessionId, Ct);

        _handler.CallCount.Should().Be(1);
        _handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/revoke/33333333-3333-3333-3333-333333333333");
        _handler.LastRequest.Body.Should().BeNull();
        _handler.LastRequest.Authorization!.Parameter.Should().Be(StoredAccessToken);
    }

    [Fact]
    public async Task RevokeSessionAsync_TreatsThe204AsSuccess()
    {
        // The non-generic reader is load-bearing here: asking for a value would turn this body-less
        // success into an "Http.EmptyResponse" failure.
        var sut = CreateSut(HttpStatusCode.NoContent);

        var result = await sut.RevokeSessionAsync(Guid.NewGuid(), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeSessionAsync_When404_ReportsANotFoundFailure()
    {
        var sut = CreateSut(HttpStatusCode.NotFound);

        var result = await sut.RevokeSessionAsync(Guid.NewGuid(), Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.404");
        error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task RevokeSessionAsync_WhenTheRequestNeverReachesAServer_ReturnsATransportFailure()
    {
        var sut = CreateSut(_ => throw new HttpRequestException("Socket closed"));

        var result = await sut.RevokeSessionAsync(Guid.NewGuid(), Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
    }

    [Fact]
    public async Task RevokeSessionAsync_WhenTheCallerCancels_StillThrows()
    {
        var sut = CreateSut(HttpStatusCode.NoContent);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.RevokeSessionAsync(Guid.NewGuid(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
