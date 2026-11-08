using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Controllers.Passkeys;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Passkeys.Tests;

/// <summary>
/// The passkey endpoints (ADR-139) driven through real routing, the framework's <c>AddPasskeys</c>
/// and a software authenticator, one test per approved assertion: disabled means no routes, a
/// register-then-sign-in mints a token pair with <c>mfa=passkey</c>, a replayed challenge is refused,
/// a counter that does not advance is refused (zero to zero allowed), the last passkey of a
/// password-less account cannot be deleted, and registration needs a fresh or MFA-carrying token.
/// </summary>
public sealed class PasskeysControllerBaseTests
{
    private const int UserId = 7;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // == With passkeys disabled, no routes ==
    public static TheoryData<string, string> EveryRoute => new()
    {
        { "POST", "/auth/passkeys/register/options" },
        { "POST", "/auth/passkeys/register" },
        { "GET", "/auth/passkeys" },
        { "PATCH", $"/auth/passkeys/{Guid.NewGuid()}" },
        { "DELETE", $"/auth/passkeys/{Guid.NewGuid()}" },
        { "POST", "/auth/passkeys/signin/options" },
        { "POST", "/auth/passkeys/signin" },
    };

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task Disabled_EveryRouteAnswers404(string method, string path)
    {
        await using var host = await PasskeyTestHost.StartAsync(enabled: false);
        using var client = host.SignedInClient(UserId);

        using var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task AddPasskeysNeverCalled_EveryRouteAnswers404(string method, string path)
    {
        await using var host = await PasskeyTestHost.StartAsync(addPasskeys: false);
        using var client = host.SignedInClient(UserId);

        using var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // == Register then sign in mints a token pair with mfa=passkey ==
    [Fact]
    public async Task RegisterThenSignIn_MintsATokenPairWhoseMfaClaimIsPasskey()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);

        await RegisterAsync(host, authenticator, signCount: 0);
        var (status, tokens) = await SignInAsync(host, authenticator, signCount: 0);

        status.Should().Be(HttpStatusCode.OK);
        tokens!.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        var accessToken = new JwtSecurityTokenHandler().ReadJwtToken(tokens.Value.AccessToken);
        accessToken.Claims.Should().ContainSingle(c => c.Type == AuthClaimTypes.MultiFactor)
            .Which.Value.Should().Be(AuthClaimTypes.MultiFactorMethodPasskey);
        accessToken.Claims.Should().Contain(c => c.Type == AuthClaimTypes.Subject && c.Value == UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        accessToken.Claims.Should().Contain(c => c.Type == AuthClaimTypes.SessionId, "the pair is a normal refresh session");
        accessToken.Claims.Should().ContainSingle(c => c.Type == AuthClaimTypes.AuthTime)
            .Which.Value.Should().Be(host.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Register_StoresThePasskeyUnderTheCallerWithItsName_AndListShowsIt()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);

        await RegisterAsync(host, authenticator, signCount: 0, name: "Work laptop");

        using var client = host.SignedInClient(UserId);
        var list = await client.GetFromJsonAsync<List<PasskeyResponse>>("/auth/passkeys", Ct);
        list.Should().ContainSingle().Which.Name.Should().Be("Work laptop");
        host.Store.Rows.Should().ContainSingle().Which.CredentialId.Should().Equal(authenticator.CredentialId);
    }

    // == A replayed challenge is refused ==
    [Fact]
    public async Task SignIn_ReplayingACompletedFlow_IsRefused()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 0);

        using var client = host.AnonymousClient();
        var options = await PostOptionsAsync(client, "/auth/passkeys/signin/options");
        var request = new PasskeySignInRequest(options.FlowId, authenticator.CreateAssertion(options.Options, signCount: 0));

        using var first = await client.PostAsJsonAsync("/auth/passkeys/signin", request, Ct);
        using var replay = await client.PostAsJsonAsync("/auth/passkeys/signin", request, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(replay)).Should().Be(PasskeyErrors.ChallengeInvalidCode);
    }

    [Fact]
    public async Task Register_ReplayingACompletedFlow_IsRefused()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        using var client = host.SignedInClient(UserId);
        var options = await PostOptionsAsync(client, "/auth/passkeys/register/options");
        var request = new PasskeyRegisterRequest(options.FlowId, authenticator.CreateAttestation(options.Options));

        using var first = await client.PostAsJsonAsync("/auth/passkeys/register", request, Ct);
        using var replay = await client.PostAsJsonAsync("/auth/passkeys/register", request, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(replay)).Should().Be(PasskeyErrors.ChallengeInvalidCode);
        host.Store.Rows.Should().ContainSingle();
    }

    // == A decreasing sign count is refused (0 to 0 allowed) ==
    [Fact]
    public async Task SignIn_WithACounterBelowTheStoredOne_IsRefused_AndTheStoredCounterIsKept()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 5);

        var (status, tokens) = await SignInAsync(host, authenticator, signCount: 3);

        status.Should().Be(HttpStatusCode.Unauthorized);
        tokens.Should().BeNull();
        host.Store.Rows.Single().SignCount.Should().Be(5);
    }

    [Fact]
    public async Task SignIn_WithACounterAboveTheStoredOne_SignsInAndRecordsIt()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 5);

        var (status, _) = await SignInAsync(host, authenticator, signCount: 6);

        status.Should().Be(HttpStatusCode.OK);
        host.Store.Rows.Single().SignCount.Should().Be(6);
        host.Store.Rows.Single().LastUsedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task SignIn_WithZeroOnBothSides_IsAllowedEveryTime()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 0);

        var (first, _) = await SignInAsync(host, authenticator, signCount: 0);
        var (second, _) = await SignInAsync(host, authenticator, signCount: 0);

        first.Should().Be(HttpStatusCode.OK);
        second.Should().Be(HttpStatusCode.OK, "a synced passkey reports zero on every use");
    }

    // == Deleting the last passkey of a password-less user is refused ==
    [Fact]
    public async Task Delete_TheOnlyPasskeyOfAnAccountWithNoPasswordAndNoExternalLogin_IsRefused()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId, hasPassword: false);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 0);
        using var client = host.SignedInClient(UserId);

        using var response = await client.DeleteAsync(Rel($"/auth/passkeys/{host.Store.Rows.Single().Id}"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be(PasskeyErrors.LastPasskeyCode);
        host.Store.Rows.Should().ContainSingle("the account's only way in must survive");
    }

    [Fact]
    public async Task Delete_TheOnlyPasskeyOfAnAccountWithAPassword_Succeeds()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId, hasPassword: true);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 0);
        using var client = host.SignedInClient(UserId);

        using var response = await client.DeleteAsync(Rel($"/auth/passkeys/{host.Store.Rows.Single().Id}"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_OneOfTwoPasskeysOfAPasswordlessAccount_Succeeds()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId, hasPassword: false);
        using var phone = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        using var laptop = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, phone, signCount: 0);
        await RegisterAsync(host, laptop, signCount: 0);
        using var client = host.SignedInClient(UserId);

        using var response = await client.DeleteAsync(Rel($"/auth/passkeys/{host.Store.Rows[0].Id}"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Store.Rows.Should().ContainSingle();
    }

    // == Registration needs a sign-in (auth_time) within the last 10 minutes ==
    [Theory]
    [InlineData("/auth/passkeys/register/options")]
    [InlineData("/auth/passkeys/register")]
    public async Task Register_WhenTheSignInIsTwentyMinutesOld_IsRefused_EvenWithABrandNewTokenAndMfa(string path)
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var fresh = host.SignedInClient(UserId);
        var options = await PostOptionsAsync(fresh, "/auth/passkeys/register/options");
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        using var stale = host.SignedInClient(UserId, signedInAgo: TimeSpan.FromMinutes(20), multiFactor: AuthClaimTypes.MultiFactorMethodTotp);

        using var response = await PostRegistrationStepAsync(stale, path, options, authenticator);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be(PasskeyErrors.StepUpRequiredCode);
        host.Store.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/auth/passkeys/register/options")]
    [InlineData("/auth/passkeys/register")]
    public async Task Register_WithATokenThatHasNoAuthTime_IsRefused(string path)
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var fresh = host.SignedInClient(UserId);
        var options = await PostOptionsAsync(fresh, "/auth/passkeys/register/options");
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        using var legacy = host.SignedInClient(UserId, multiFactor: AuthClaimTypes.MultiFactorMethodTotp, withAuthTime: false);

        using var response = await PostRegistrationStepAsync(legacy, path, options, authenticator);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a token minted before auth_time shipped cannot prove a recent sign-in");
        (await ProblemCodeAsync(response)).Should().Be(PasskeyErrors.StepUpRequiredCode);
    }

    [Fact]
    public async Task Register_WithTheTokenOfAFreshSignIn_IsAllowed()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var first = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, first, signCount: 0);
        var (_, tokens) = await SignInAsync(host, first, signCount: 0);

        using var client = host.BearerClient(tokens!.Value.AccessToken);
        using var second = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        var options = await PostOptionsAsync(client, "/auth/passkeys/register/options");
        using var response = await client.PostAsJsonAsync(
            "/auth/passkeys/register", new PasskeyRegisterRequest(options.FlowId, second.CreateAttestation(options.Options)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Register_AfterARefreshOfATwentyMinuteOldSignIn_IsRefused_ThoughTheAccessTokenIsBrandNew()
    {
        await using var host = await PasskeyTestHost.StartAsync();
        host.Users.Add(UserId);
        using var authenticator = new SoftwareAuthenticator(PasskeyTestHost.RelyingPartyId, PasskeyTestHost.Origin);
        await RegisterAsync(host, authenticator, signCount: 0);
        var (_, signedIn) = await SignInAsync(host, authenticator, signCount: 0);
        var signInTime = AuthTimeOf(signedIn!.Value.AccessToken);

        host.Clock.Advance(TimeSpan.FromMinutes(20));
        var refreshed = await host.RefreshAsync(signedIn.Value);

        refreshed.AccessToken.Should().NotBe(signedIn.Value.AccessToken, "the refresh mints a brand-new access token");
        AuthTimeOf(refreshed.AccessToken).Should().Be(signInTime, "a refresh carries the sign-in instant over unchanged");
        new JwtSecurityTokenHandler().ReadJwtToken(refreshed.AccessToken).Claims
            .Should().Contain(c => c.Type == AuthClaimTypes.MultiFactor, "mfa rides the refresh too, and must not stand in for a recent sign-in");
        using var client = host.BearerClient(refreshed.AccessToken);
        using var response = await client.PostAsync(Rel("/auth/passkeys/register/options"), content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be(PasskeyErrors.StepUpRequiredCode);
    }

    private static string? AuthTimeOf(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.SingleOrDefault(c => c.Type == AuthClaimTypes.AuthTime)?.Value;

    private static async Task<HttpResponseMessage> PostRegistrationStepAsync(
        HttpClient client,
        string path,
        PasskeyOptionsResponse options,
        SoftwareAuthenticator authenticator) =>
        path.EndsWith("/options", StringComparison.Ordinal)
            ? await client.PostAsync(Rel(path), content: null, Ct)
            : await client.PostAsJsonAsync(path, new PasskeyRegisterRequest(options.FlowId, authenticator.CreateAttestation(options.Options)), Ct);
    // == Attribute contract: anonymous sign-in on the auth-ip policy, the rest signed in ==
    [Theory]
    [InlineData("signin/options")]
    [InlineData("signin")]
    public void SignInActions_AreAnonymousAndThrottledByTheAuthIpPolicy(string template)
    {
        var action = ActionByTemplate(template);

        action.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
            .Should().Be(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp);
    }

    [Theory]
    [InlineData("register/options")]
    [InlineData("register")]
    [InlineData("{id:guid}")]
    public void ManagementActions_RequireASignedInCaller(string template)
    {
        var actions = typeof(PasskeysControllerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.Template == template))
            .ToList();

        actions.Should().NotBeEmpty();
        actions.Should().AllSatisfy(a => a.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull());
    }

    private static MethodInfo ActionByTemplate(string template) =>
        typeof(PasskeysControllerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.Template == template));

    private static async Task RegisterAsync(PasskeyTestHost host, SoftwareAuthenticator authenticator, uint signCount, string? name = null)
    {
        using var client = host.SignedInClient(UserId);
        var options = await PostOptionsAsync(client, "/auth/passkeys/register/options");
        using var response = await client.PostAsJsonAsync(
            "/auth/passkeys/register",
            new PasskeyRegisterRequest(options.FlowId, authenticator.CreateAttestation(options.Options, signCount), name),
            Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<(HttpStatusCode Status, AuthenticationResponse? Tokens)> SignInAsync(
        PasskeyTestHost host,
        SoftwareAuthenticator authenticator,
        uint signCount)
    {
        using var client = host.AnonymousClient();
        var options = await PostOptionsAsync(client, "/auth/passkeys/signin/options");
        using var response = await client.PostAsJsonAsync(
            "/auth/passkeys/signin",
            new PasskeySignInRequest(options.FlowId, authenticator.CreateAssertion(options.Options, signCount)),
            Ct);

        return response.IsSuccessStatusCode
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<AuthenticationResponse>(Ct))
            : (response.StatusCode, null);
    }

    private static async Task<PasskeyOptionsResponse> PostOptionsAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsync(Rel(path), content: null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PasskeyOptionsResponse>(Ct))!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        return problem?.Extensions.TryGetValue("errors", out var errors) == true
            && errors is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } array
            && array.GetArrayLength() > 0
            && array[0].TryGetProperty("code", out var code)
                ? code.GetString()
                : problem?.Title;
    }

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (string.Equals(method, "POST", StringComparison.Ordinal) || string.Equals(method, "PATCH", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new { flowId = "x", name = "x", credential = new { } });
        }

        return await client.SendAsync(request, Ct);
    }
}
