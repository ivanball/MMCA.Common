using System.Security.Claims;
using AspNet.Security.OAuth.Apple;
using AspNet.Security.OAuth.GitHub;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MMCA.Common.API.Authentication;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;
using AspNetAuthenticationService = Microsoft.AspNetCore.Authentication.IAuthenticationService;
using IAuthenticationService = MMCA.Common.Application.Auth.IAuthenticationService;

namespace MMCA.Common.API.Tests.Controllers.Auth;

/// <summary>
/// Covers the OAuth completion flow of <see cref="OAuthControllerBase"/>: provider challenges,
/// the callback completion (error redirects, claim extraction, name splitting), the single-use
/// exchange-code round trip, and the security invariant that access/refresh tokens never appear
/// in a redirect URL (they only travel through the out-of-band exchange endpoint).
/// </summary>
public sealed class OAuthControllerBaseTests
{
    private const string UIBaseUrl = "https://ui.example.com";
    private const string ExchangeCodePrefix = "oauth-exchange:";

    // The display-name claim the GitHub handler maps the profile "name" into (login handle is ClaimTypes.Name).
    private const string GitHubDisplayNameClaimType = "urn:github:name";

    // ── Mocks ──
    private sealed record Mocks(
        Mock<IAuthenticationService> AuthService,
        Mock<ICacheService> CacheService,
        Mock<AspNetAuthenticationService> HttpAuth);

    // ── Factory ──
    private static (TestOAuthController Sut, Mocks Mocks) CreateSut(
        string? uiBaseUrl = UIBaseUrl,
        string[]? allowedReturnUrlSchemes = null)
    {
        var authService = new Mock<IAuthenticationService>();
        var cacheService = new Mock<ICacheService>();
        var httpAuth = new Mock<AspNetAuthenticationService>();

        var configValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (uiBaseUrl is not null)
        {
            configValues["OAuth:UIBaseUrl"] = uiBaseUrl;
        }

        for (var i = 0; i < (allowedReturnUrlSchemes?.Length ?? 0); i++)
        {
            configValues["OAuth:AllowedReturnUrlSchemes:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                allowedReturnUrlSchemes![i];
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new SingleServiceProvider(httpAuth.Object),
        };

        var sut = new TestOAuthController(authService.Object, cacheService.Object, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (sut, new Mocks(authService, cacheService, httpAuth));
    }

    private static ClaimsPrincipal CreatePrincipal(
        string? providerKey = "provider-key-1",
        string? email = "user@example.com",
        string authenticationType = "Google",
        IReadOnlyList<Claim>? nameClaims = null)
    {
        var claims = new List<Claim>();
        if (providerKey is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, providerKey));
        }

        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        claims.AddRange(nameClaims ?? []);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    private static void SetupExternalAuthentication(Mocks mocks, AuthenticateResult result) =>
        mocks.HttpAuth
            .Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), ExternalAuthExtensions.ExternalLoginScheme))
            .ReturnsAsync(result);

    private static AuthenticateResult SuccessfulAuthentication(ClaimsPrincipal principal, string returnUrl = "/")
    {
        var properties = new AuthenticationProperties { Items = { ["returnUrl"] = returnUrl } };
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, properties, ExternalAuthExtensions.ExternalLoginScheme));
    }

    private static AuthenticationResponse CreateAuthResponse() =>
        new("the-access-token", "the-refresh-token", DateTime.UtcNow.AddHours(1));

    // ── GoogleLogin / GitHubLogin challenges ──
    [Fact]
    public void GoogleLogin_ReturnsChallengeForGoogleSchemeCarryingReturnUrl()
    {
        var (sut, _) = CreateSut();

        var result = sut.GoogleLogin(new Uri("https://app.example.com/dashboard"));

        result.AuthenticationSchemes.Should().ContainSingle()
            .Which.Should().Be(GoogleDefaults.AuthenticationScheme);
        result.Properties!.RedirectUri.Should().Be("/auth/oauth/complete");
        result.Properties.Items["returnUrl"].Should().Be("https://app.example.com/dashboard");
    }

    [Fact]
    public void GitHubLogin_WithoutReturnUrl_DefaultsReturnUrlToRoot()
    {
        var (sut, _) = CreateSut();

        var result = sut.GitHubLogin(returnUrl: null);

        result.AuthenticationSchemes.Should().ContainSingle()
            .Which.Should().Be(GitHubAuthenticationDefaults.AuthenticationScheme);
        result.Properties!.RedirectUri.Should().Be("/auth/oauth/complete");
        result.Properties.Items["returnUrl"].Should().Be("/");
    }

    [Fact]
    public void AppleLogin_ReturnsChallengeForAppleSchemeCarryingReturnUrl()
    {
        var (sut, _) = CreateSut();

        var result = sut.AppleLogin(new Uri("https://app.example.com/dashboard"));

        result.AuthenticationSchemes.Should().ContainSingle()
            .Which.Should().Be(AppleAuthenticationDefaults.AuthenticationScheme);
        result.Properties!.RedirectUri.Should().Be("/auth/oauth/complete");
        result.Properties.Items["returnUrl"].Should().Be("https://app.example.com/dashboard");
    }

    // ── CompleteAsync: error paths ──
    [Fact]
    public async Task CompleteAsync_WhenExternalAuthenticationFails_RedirectsToLoginWithOAuthFailedError()
    {
        var (sut, mocks) = CreateSut();
        SetupExternalAuthentication(mocks, AuthenticateResult.Fail("provider rejected"));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be($"{UIBaseUrl}/login?error=oauth_failed");
    }

    [Fact]
    public async Task CompleteAsync_WithTrailingSlashesOnUIBaseUrl_TrimsThemFromRedirect()
    {
        var (sut, mocks) = CreateSut(uiBaseUrl: $"{UIBaseUrl}///");
        SetupExternalAuthentication(mocks, AuthenticateResult.Fail("provider rejected"));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be($"{UIBaseUrl}/login?error=oauth_failed");
    }

    [Fact]
    public async Task CompleteAsync_WithoutUIBaseUrlConfigured_RedirectsRelativeToRoot()
    {
        var (sut, mocks) = CreateSut(uiBaseUrl: null);
        SetupExternalAuthentication(mocks, AuthenticateResult.Fail("provider rejected"));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/login?error=oauth_failed");
    }

    [Fact]
    public async Task CompleteAsync_WhenProviderKeyClaimMissing_RedirectsWithMissingClaimsError()
    {
        var (sut, mocks) = CreateSut();
        SetupExternalAuthentication(
            mocks, SuccessfulAuthentication(CreatePrincipal(providerKey: null)));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be($"{UIBaseUrl}/login?error=missing_claims");
        mocks.AuthService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompleteAsync_WhenEmailClaimMissing_RedirectsWithMissingClaimsError()
    {
        var (sut, mocks) = CreateSut();
        SetupExternalAuthentication(
            mocks, SuccessfulAuthentication(CreatePrincipal(email: null)));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be($"{UIBaseUrl}/login?error=missing_claims");
    }

    [Fact]
    public async Task CompleteAsync_WhenExternalLoginFails_RedirectsWithFirstErrorCode()
    {
        var (sut, mocks) = CreateSut();
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(CreatePrincipal()));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
            [
                Error.Conflict("Auth.AccountLocked", "Account is locked"),
                Error.Failure("Auth.Secondary", "secondary error"),
            ]));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be($"{UIBaseUrl}/login?error=Auth.AccountLocked");
    }

    // ── CompleteAsync: success path ──
    [Fact]
    public async Task CompleteAsync_OnSuccess_RedirectsWithSingleUseCodeAndNeverPutsTokensInTheUrl()
    {
        var (sut, mocks) = CreateSut();
        var principal = CreatePrincipal(
            nameClaims:
            [
                new Claim(ClaimTypes.GivenName, "Jane"),
                new Claim(ClaimTypes.Surname, "Doe"),
            ]);
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(principal, returnUrl: "/dashboard"));

        AuthenticationResponse authResponse = CreateAuthResponse();
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                "Google", "provider-key-1", "user@example.com", "Jane", "Doe", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponse));

        string? cachedKey = null;
        AuthenticationResponse cachedValue = default;
        TimeSpan? cachedExpiration = null;
        mocks.CacheService
            .Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<AuthenticationResponse>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string key, AuthenticationResponse value, TimeSpan? expiration, CancellationToken _) =>
            {
                cachedKey = key;
                cachedValue = value;
                cachedExpiration = expiration;
            })
            .Returns(Task.CompletedTask);

        var result = await sut.CompleteAsync();

        // The redirect carries only the opaque single-use code, never the token pair.
        var url = result.Should().BeOfType<RedirectResult>().Which.Url;
        url.Should().StartWith($"{UIBaseUrl}/auth/oauth-complete?code=");
        url.Should().EndWith($"&returnUrl={Uri.EscapeDataString("/dashboard")}");
        url.Should().NotContain("the-access-token").And.NotContain("the-refresh-token");

        var code = url.Split("code=")[1].Split('&')[0];
        code.Should().HaveLength(64).And.MatchRegex("^[0-9A-F]{64}$");

        // The token pair waits server-side under the same code, with the short exchange TTL.
        cachedKey.Should().Be(ExchangeCodePrefix + code);
        cachedValue.Should().Be(authResponse);
        cachedExpiration.Should().Be(TimeSpan.FromMinutes(2));

        // The temporary external-login cookie is cleared once the local pair is minted.
        mocks.HttpAuth.Verify(
            x => x.SignOutAsync(
                It.IsAny<HttpContext>(),
                ExternalAuthExtensions.ExternalLoginScheme,
                It.IsAny<AuthenticationProperties?>()),
            Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_WhenReturnUrlItemMissing_FallsBackToRootInsteadOfThrowing()
    {
        // A ticket whose AuthenticationProperties carries no "returnUrl" item (a challenge issued
        // outside ChallengeProvider, or properties lost across the provider round trip) must complete
        // with the "/" fallback rather than throwing KeyNotFoundException on the Items indexer.
        var (sut, mocks) = CreateSut();
        var properties = new AuthenticationProperties(); // deliberately no returnUrl item
        SetupExternalAuthentication(mocks, AuthenticateResult.Success(
            new AuthenticationTicket(CreatePrincipal(), properties, ExternalAuthExtensions.ExternalLoginScheme)));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreateAuthResponse()));

        var result = await sut.CompleteAsync();

        var url = result.Should().BeOfType<RedirectResult>().Which.Url;
        url.Should().StartWith($"{UIBaseUrl}/auth/oauth-complete?code=");
        url.Should().EndWith($"&returnUrl={Uri.EscapeDataString("/")}");
    }

    // ── CompleteAsync: name extraction fallbacks ──
    [Theory]
    [InlineData("Jane", "Doe", null, "Jane", "Doe")]
    [InlineData(null, null, "John Smith", "John", "Smith")]
    [InlineData(null, null, "Mary Jane Watson", "Mary", "Jane Watson")]
    [InlineData(null, null, "Prince", "User", "User")]
    [InlineData(null, null, null, "User", "User")]
    [InlineData("Jane", null, null, "Jane", "User")]
    public async Task CompleteAsync_ExtractsNameFromClaimsWithFullNameFallback(
        string? givenName,
        string? surname,
        string? fullName,
        string expectedFirstName,
        string expectedLastName)
    {
        var (sut, mocks) = CreateSut();
        var nameClaims = new List<Claim>();
        if (givenName is not null)
        {
            nameClaims.Add(new Claim(ClaimTypes.GivenName, givenName));
        }

        if (surname is not null)
        {
            nameClaims.Add(new Claim(ClaimTypes.Surname, surname));
        }

        if (fullName is not null)
        {
            nameClaims.Add(new Claim(ClaimTypes.Name, fullName));
        }

        SetupExternalAuthentication(
            mocks, SuccessfulAuthentication(CreatePrincipal(nameClaims: nameClaims)));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(Error.Failure("Test.Stop", "stop here")));

        await sut.CompleteAsync();

        mocks.AuthService.Verify(
            x => x.ExternalLoginAsync(
                "Google",
                "provider-key-1",
                "user@example.com",
                expectedFirstName,
                expectedLastName,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── CompleteAsync: the REAL GitHub claim shape (U-15/A-46) ──
    // The GitHub handler maps ClaimTypes.Name to the LOGIN handle (never contains a space) and puts
    // the display name in "urn:github:name". It never issues GivenName/Surname. A last name that
    // reaches ExternalLoginAsync empty fails the user invariants, so every first GitHub sign-up
    // bounced back to /login?error=User.LastName.Empty.
    [Fact]
    public async Task CompleteAsync_GitHubPrincipal_TakesTheNameFromTheDisplayNameClaimNotTheLoginHandle()
    {
        var (sut, mocks) = CreateSut();
        var captured = CaptureExternalLoginNames(mocks);
        SetupExternalAuthentication(
            mocks,
            SuccessfulAuthentication(GitHubPrincipal(
                new Claim(ClaimTypes.Name, "octocat"),
                new Claim(GitHubDisplayNameClaimType, "Mona Lisa Octocat"))));

        await sut.CompleteAsync();

        captured.Should().ContainSingle();
        captured[0].FirstName.Should().Be("Mona", "the first name comes from the GitHub display name, not the login handle");
        captured[0].LastName.Should().Be("Lisa Octocat", "the rest of the GitHub display name is the last name");
    }

    [Fact]
    public async Task CompleteAsync_GitHubPrincipalWithoutADisplayName_NeverPassesAnEmptyLastName()
    {
        var (sut, mocks) = CreateSut();
        var captured = CaptureExternalLoginNames(mocks);
        SetupExternalAuthentication(
            mocks,
            SuccessfulAuthentication(GitHubPrincipal(new Claim(ClaimTypes.Name, "octocat"))));

        await sut.CompleteAsync();

        captured.Should().ContainSingle();
        captured[0].LastName.Should().NotBeNullOrWhiteSpace(
            "a GitHub account with no public display name must still be able to sign up; an empty last name fails the user invariants");
    }

    [Fact]
    public async Task CompleteAsync_GitHubPrincipalWithASingleTokenDisplayName_NeverPassesAnEmptyLastName()
    {
        var (sut, mocks) = CreateSut();
        var captured = CaptureExternalLoginNames(mocks);
        SetupExternalAuthentication(
            mocks,
            SuccessfulAuthentication(GitHubPrincipal(
                new Claim(ClaimTypes.Name, "prince"),
                new Claim(GitHubDisplayNameClaimType, "Prince"))));

        await sut.CompleteAsync();

        captured.Should().ContainSingle();
        captured[0].LastName.Should().NotBeNullOrWhiteSpace(
            "a one-word display name must still yield a usable (placeholder) last name");
    }

    private static ClaimsPrincipal GitHubPrincipal(params Claim[] nameClaims) =>
        CreatePrincipal(
            providerKey: "583231",
            email: "octocat@github.com",
            authenticationType: GitHubAuthenticationDefaults.AuthenticationScheme,
            nameClaims: nameClaims);

    private static List<(string FirstName, string LastName)> CaptureExternalLoginNames(Mocks mocks)
    {
        var captured = new List<(string FirstName, string LastName)>();
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, CancellationToken>(
                (_, _, _, firstName, lastName, _) => captured.Add((firstName, lastName)))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(Error.Failure("Test.Stop", "stop here")));
        return captured;
    }

    [Fact]
    public async Task CompleteAsync_UsesIdentityAuthenticationTypeAsProviderName()
    {
        var (sut, mocks) = CreateSut();
        SetupExternalAuthentication(
            mocks, SuccessfulAuthentication(CreatePrincipal(authenticationType: "GitHub")));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(Error.Failure("Test.Stop", "stop here")));

        await sut.CompleteAsync();

        mocks.AuthService.Verify(
            x => x.ExternalLoginAsync(
                "GitHub",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── CompleteAsync: native custom-scheme returnUrl allowlist (ADR-044) ──
    [Fact]
    public async Task CompleteAsync_WithAllowListedSchemeReturnUrl_RedirectsToNativeCallbackWithCodeOnly()
    {
        var (sut, mocks) = CreateSut(allowedReturnUrlSchemes: ["myapp"]);
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(
            CreatePrincipal(), returnUrl: "myapp://oauth-complete"));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreateAuthResponse()));

        var result = await sut.CompleteAsync();

        var url = result.Should().BeOfType<RedirectResult>().Which.Url;
        url.Should().StartWith("myapp://oauth-complete?code=");
        url.Should().NotContain("returnUrl=", "the native callback IS the destination")
            .And.NotContain("the-access-token")
            .And.NotContain("the-refresh-token");

        var code = url.Split("code=")[1];
        code.Should().HaveLength(64).And.MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public async Task CompleteAsync_SchemeMatchIsCaseInsensitive()
    {
        var (sut, mocks) = CreateSut(allowedReturnUrlSchemes: ["MyApp"]);
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(
            CreatePrincipal(), returnUrl: "myapp://oauth-complete"));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreateAuthResponse()));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith("myapp://oauth-complete?code=");
    }

    [Fact]
    public async Task CompleteAsync_WithCustomSchemeButEmptyAllowlist_KeepsTheWebRedirect()
    {
        // The default (no OAuth:AllowedReturnUrlSchemes configured) must behave exactly as before:
        // custom-scheme return URLs flow to the pinned web UI as an opaque returnUrl parameter.
        var (sut, mocks) = CreateSut();
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(
            CreatePrincipal(), returnUrl: "myapp://oauth-complete"));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreateAuthResponse()));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith($"{UIBaseUrl}/auth/oauth-complete?code=");
    }

    [Fact]
    public async Task CompleteAsync_HttpsReturnUrl_NeverMatchesTheAllowlistEvenIfListed()
    {
        // Web destinations always flow through the config-pinned UIBaseUrl; listing "https" must
        // not turn the allowlist into an open redirect to an arbitrary host.
        var (sut, mocks) = CreateSut(allowedReturnUrlSchemes: ["https"]);
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(
            CreatePrincipal(), returnUrl: "https://evil.example.com/steal"));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreateAuthResponse()));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith($"{UIBaseUrl}/auth/oauth-complete?code=");
    }

    [Fact]
    public async Task CompleteAsync_WhenExternalLoginFailsWithAllowListedScheme_SendsErrorToNativeCallback()
    {
        var (sut, mocks) = CreateSut(allowedReturnUrlSchemes: ["myapp"]);
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(
            CreatePrincipal(), returnUrl: "myapp://oauth-complete"));
        mocks.AuthService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Conflict("Auth.AccountLocked", "Account is locked")));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("myapp://oauth-complete?error=Auth.AccountLocked");
    }

    [Fact]
    public async Task CompleteAsync_WhenClaimsMissingWithAllowListedScheme_SendsErrorToNativeCallback()
    {
        var (sut, mocks) = CreateSut(allowedReturnUrlSchemes: ["myapp"]);
        SetupExternalAuthentication(mocks, SuccessfulAuthentication(
            CreatePrincipal(providerKey: null), returnUrl: "myapp://oauth-complete"));

        var result = await sut.CompleteAsync();

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("myapp://oauth-complete?error=missing_claims");
    }

    [Fact]
    public async Task CompleteAsync_WithMockedConfigurationReturningNullSections_DoesNotThrow()
    {
        // Consumer test suites (ADC's OAuthControllerTests) construct the controller over a loose
        // Mock<IConfiguration>, whose GetSection returns null. The allowlist lookup must treat that
        // exactly like an empty allowlist instead of throwing from ConfigurationBinder (the
        // v1.112.0 sweep regression this pins).
        var authService = new Mock<IAuthenticationService>();
        var cacheService = new Mock<ICacheService>();
        var httpAuth = new Mock<AspNetAuthenticationService>();
        var configuration = new Mock<IConfiguration>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new SingleServiceProvider(httpAuth.Object),
        };
        var sut = new TestOAuthController(authService.Object, cacheService.Object, configuration.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        httpAuth
            .Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), ExternalAuthExtensions.ExternalLoginScheme))
            .ReturnsAsync(SuccessfulAuthentication(CreatePrincipal(), returnUrl: "myapp://oauth-complete"));
        authService
            .Setup(x => x.ExternalLoginAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreateAuthResponse()));

        var result = await sut.CompleteAsync();

        // Null sections = empty allowlist: even a custom-scheme returnUrl flows to the web redirect.
        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith("/auth/oauth-complete?code=");
    }

    // ── ExchangeAsync ──
    [Fact]
    public async Task ExchangeAsync_WithWhitespaceCode_ReturnsBadRequestWithoutTouchingTheCache()
    {
        var (sut, mocks) = CreateSut();

        var result = await sut.ExchangeAsync(new OAuthCodeExchangeRequest("   "), CancellationToken.None);

        var problem = result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Title.Should().Be("Invalid sign-in code");
        mocks.CacheService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExchangeAsync_WhenCodeUnknownOrExpired_ReturnsBadRequestAndDoesNotBurnAnything()
    {
        var (sut, mocks) = CreateSut();
        mocks.CacheService
            .Setup(x => x.GetFromSharedStoreAsync<AuthenticationResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(AuthenticationResponse));

        var result = await sut.ExchangeAsync(new OAuthCodeExchangeRequest("UNKNOWN"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        mocks.CacheService.Verify(
            x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExchangeAsync_WithValidCode_ReturnsTokenPairAndBurnsTheCode()
    {
        var (sut, mocks) = CreateSut();
        AuthenticationResponse authResponse = CreateAuthResponse();
        const string code = "ABCDEF0123456789";
        mocks.CacheService
            .Setup(x => x.GetFromSharedStoreAsync<AuthenticationResponse>(
                ExchangeCodePrefix + code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(authResponse);

        var result = await sut.ExchangeAsync(new OAuthCodeExchangeRequest(code), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(authResponse);
        mocks.CacheService.Verify(
            x => x.RemoveAsync(ExchangeCodePrefix + code, It.IsAny<CancellationToken>()),
            Times.Once,
            "the code is single-use: a replayed code must not mint a second token pair");
    }

    [Fact]
    public async Task ExchangeAsync_ReadsTheCodeFromTheSharedStore_NeverThroughGetAsync()
    {
        // GetAsync may answer from a replica's in-process copy of a code another replica already
        // burned, which would mint the token pair twice.
        var (sut, mocks) = CreateSut();
        AuthenticationResponse authResponse = CreateAuthResponse();
        const string code = "ABCDEF0123456789";
        mocks.CacheService
            .Setup(x => x.GetFromSharedStoreAsync<AuthenticationResponse>(
                ExchangeCodePrefix + code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(authResponse);

        var result = await sut.ExchangeAsync(new OAuthCodeExchangeRequest(code), CancellationToken.None);

        mocks.CacheService.Verify(
            x => x.GetAsync<AuthenticationResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(authResponse);
    }

    // ── ExchangeAsync under a distributed lock (multi-replica) ──
    // Two replicas redeeming one code at the same instant could both read it before either removed
    // it, minting the token pair twice. The read and the burn are one critical section under
    // IDistributedLock "lock:" + the exchange key; a contended redeem is an invalid code.
    [Fact]
    public async Task ExchangeAsync_WhenTheRedeemLockIsContended_ReturnsInvalidCode_WithoutReadingOrBurning()
    {
        const string code = "ABCDEF0123456789";
        var (sut, cache, distributedLock) = CreateLockingSut();
        cache
            .Setup(x => x.GetFromSharedStoreAsync<AuthenticationResponse>(ExchangeCodePrefix + code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAuthResponse());
        distributedLock
            .Setup(x => x.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var result = await sut.ExchangeAsync(new OAuthCodeExchangeRequest(code), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>()
            .Which.Title.Should().Be("Invalid sign-in code");
        distributedLock.Verify(
            x => x.TryAcquireAsync("lock:" + ExchangeCodePrefix + code, It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
        cache.Verify(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExchangeAsync_UnderTheRedeemLock_RedeemsACodeOnce_AndASecondExchangeFails()
    {
        const string code = "ABCDEF0123456789";
        const string exchangeKey = ExchangeCodePrefix + code;
        var (sut, cache, distributedLock) = CreateLockingSut();
        AuthenticationResponse authResponse = CreateAuthResponse();
        var steps = new List<string>();
        var burned = false;

        cache
            .Setup(x => x.GetFromSharedStoreAsync<AuthenticationResponse>(exchangeKey, It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("read"))
            .ReturnsAsync(() => burned ? default : authResponse);
        cache
            .Setup(x => x.RemoveAsync(exchangeKey, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                steps.Add("burn");
                burned = true;
            })
            .Returns(Task.CompletedTask);
        distributedLock
            .Setup(x => x.TryAcquireAsync("lock:" + exchangeKey, It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("acquire"))
            .ReturnsAsync(() => new RecordingHandle(() => steps.Add("release")));

        var first = await sut.ExchangeAsync(new OAuthCodeExchangeRequest(code), CancellationToken.None);
        var second = await sut.ExchangeAsync(new OAuthCodeExchangeRequest(code), CancellationToken.None);

        first.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(authResponse);
        second.Should().BeOfType<BadRequestObjectResult>("the code was burned by the first exchange");
        steps.Should().Equal(
            ["acquire", "read", "burn", "release", "acquire", "read", "release"],
            "the read and the burn both happen inside the lock, and every acquired lock is released");
    }

    private static (LockingTestOAuthController Sut, Mock<ICacheService> Cache, Mock<IDistributedLock> Lock) CreateLockingSut()
    {
        var cacheService = new Mock<ICacheService>();
        var distributedLock = new Mock<IDistributedLock>();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["OAuth:UIBaseUrl"] = UIBaseUrl })
            .Build();

        var sut = new LockingTestOAuthController(
            new Mock<IAuthenticationService>().Object,
            cacheService.Object,
            configuration,
            distributedLock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new SingleServiceProvider(new Mock<AspNetAuthenticationService>().Object),
                },
            },
        };

        return (sut, cacheService, distributedLock);
    }

    private sealed class RecordingHandle(Action onRelease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onRelease();
            return ValueTask.CompletedTask;
        }
    }

    // ── Test double: minimal request-services provider for the authentication extensions ──
    private sealed class SingleServiceProvider(AspNetAuthenticationService authenticationService) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(AspNetAuthenticationService) ? authenticationService : null;
    }
}

// [ApiController] mirrors the attribute set the XML docs on OAuthControllerBase tell consumers to
// apply to their derived controller, and satisfies AV0014 from the Asp.Versioning analyzers.
[ApiController]
internal sealed class TestOAuthController(
    IAuthenticationService authenticationService,
    ICacheService cacheService,
    IConfiguration configuration)
    : OAuthControllerBase(authenticationService, cacheService, configuration);

// The same controller handed an IDistributedLock through the base's optional constructor parameter,
// the way a consumer's DI-activated subclass receives it.
[ApiController]
internal sealed class LockingTestOAuthController(
    IAuthenticationService authenticationService,
    ICacheService cacheService,
    IConfiguration configuration,
    IDistributedLock distributedLock)
    : OAuthControllerBase(authenticationService, cacheService, configuration, distributedLock);
