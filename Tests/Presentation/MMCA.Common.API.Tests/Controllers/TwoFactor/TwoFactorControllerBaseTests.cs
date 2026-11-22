using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.TwoFactor;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Tests.Controllers.TwoFactor;

/// <summary>
/// The self-service two-factor endpoints over a real host: each action dispatches the command its
/// factory builds and maps success and failure to the designed status; starting and confirming an
/// enrollment need a sign-in from the last ten minutes; responses that carry a secret or codes are
/// never cached; a machine client is refused; and every action is plain <c>[Authorize]</c>, so a role
/// that must present a second factor can still reach them to enroll one.
/// </summary>
public sealed class TwoFactorControllerBaseTests : IAsyncLifetime
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(20);

    private TwoFactorControllerTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> EveryRoute => new()
    {
        { "GET", TwoFactorControllerTestHost.Route },
        { "POST", TwoFactorControllerTestHost.Route + "/enrollment" },
        { "POST", TwoFactorControllerTestHost.Route + "/enrollment/confirm" },
        { "POST", TwoFactorControllerTestHost.Route + "/recovery-codes" },
        { "POST", TwoFactorControllerTestHost.Route + "/disable" },
    };

    public async ValueTask InitializeAsync() => _host = await TwoFactorControllerTestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // -- Attribute contract --
    [Fact]
    public void EveryAction_IsPlainAuthorize_AndNoneCarriesAPermission()
    {
        var actions = ActionMethods();

        actions.Should().HaveCount(5);
        actions.Should().AllSatisfy(action =>
        {
            action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Should().ContainSingle("every action needs a signed-in caller")
                .Which.Should().NotBeOfType<HasPermissionAttribute>("a permission would put enrollment behind the role rule it exists to satisfy");
            action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        });
        typeof(TwoFactorControllerBase<,,,>).GetCustomAttributes<HasPermissionAttribute>(inherit: true).Should().BeEmpty();
    }

    // -- Who may call --
    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_RefusesAMachineClient(string method, string path)
    {
        using var client = _host.SignedIn(Fresh, new Claim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine));

        using var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be(RefuseMachineClientsAttribute.ErrorCode);
        NothingDispatched();
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_ChallengesAnAnonymousCaller(string method, string path)
    {
        using var client = _host.Anonymous();

        using var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        NothingDispatched();
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_WithNoUserIdOnTheToken_Answers401(string method, string path)
    {
        using var client = _host.WithClaims(new Claim(AuthClaimTypes.AuthTime, _host.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        using var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        NothingDispatched();
    }

    // -- Status --
    [Fact]
    public async Task Status_WhenEnabled_ReportsTheRemainingRecoveryCodes()
    {
        _host.Store.State = new TwoFactorState(IsTwoFactorEnabled: true, "secret", ["h1", "h2", "h3"]);
        using var client = _host.SignedIn(Stale);

        var status = await client.GetFromJsonAsync<TwoFactorStatusResponse>(TwoFactorControllerTestHost.Route, Ct);

        status.Should().Be(new TwoFactorStatusResponse(IsEnabled: true, RecoveryCodesRemaining: 3));
    }

    [Fact]
    public async Task Status_WhenNotEnabled_ReportsNoRecoveryCodes_EvenWithLeftoverHashes()
    {
        _host.Store.State = new TwoFactorState(IsTwoFactorEnabled: false, "pending-secret", ["stale-1", "stale-2"]);
        using var client = _host.SignedIn(Stale);

        var status = await client.GetFromJsonAsync<TwoFactorStatusResponse>(TwoFactorControllerTestHost.Route, Ct);

        status.Should().Be(new TwoFactorStatusResponse(IsEnabled: false, RecoveryCodesRemaining: 0));
    }

    [Fact]
    public async Task Status_WhenTheStoreHasNoAccount_Answers404()
    {
        _host.Store.State = null;
        using var client = _host.SignedIn(Fresh);

        using var response = await client.GetAsync(new Uri(TwoFactorControllerTestHost.Route, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -- Begin enrollment --
    [Fact]
    public async Task Begin_WithARecentSignIn_DispatchesForTheCaller_AndReturnsTheSecretUncached()
    {
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/enrollment", body: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorSetupResponse>(Ct)).Should().Be(_host.Begin.Answer.Value);
        _host.Begin.Received.Should().ContainSingle().Which.UserId.Should().Be(TwoFactorControllerTestHost.UserId);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Begin_WhenAlreadyEnabled_Answers409WithTheCode()
    {
        _host.Begin.Answer = Result.Failure<TwoFactorSetupResponse>(Error.Conflict("Authentication.TwoFactorAlreadyEnabled", "Already on."));
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/enrollment", body: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("Authentication.TwoFactorAlreadyEnabled");
    }

    [Theory]
    [InlineData("/enrollment")]
    [InlineData("/enrollment/confirm")]
    public async Task BeginAndConfirm_WithASignInOlderThanTenMinutes_AreRefusedWithoutDispatching(string action)
    {
        using var client = _host.SignedIn(Stale, new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp));

        using var response = await PostAsync(client, action, new TwoFactorCodeRequest("123456"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be(TwoFactorErrors.RecentSignInRequiredCode);
        NothingDispatched();
    }

    [Theory]
    [InlineData("/enrollment")]
    [InlineData("/enrollment/confirm")]
    public async Task BeginAndConfirm_WithNoSignInTimeOnTheToken_AreRefusedWithoutDispatching(string action)
    {
        using var client = _host.SignedIn(signedInAgo: null);

        using var response = await PostAsync(client, action, new TwoFactorCodeRequest("123456"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be(TwoFactorErrors.RecentSignInRequiredCode);
        NothingDispatched();
    }

    // -- Confirm enrollment --
    [Fact]
    public async Task Confirm_WithARecentSignIn_DispatchesTheCode_AndReturnsTheRecoveryCodesUncached()
    {
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/enrollment/confirm", new TwoFactorCodeRequest("654321"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesResponse>(Ct))!.RecoveryCodes.Should().Equal("code-1", "code-2");
        var command = _host.Confirm.Received.Should().ContainSingle().Subject;
        command.UserId.Should().Be(TwoFactorControllerTestHost.UserId);
        command.Request.Code.Should().Be("654321");
        AssertNoStore(response);
    }

    public static TheoryData<string, HttpStatusCode> ConfirmFailures => new()
    {
        { "invalid", HttpStatusCode.Unauthorized },
        { "missing", HttpStatusCode.Conflict },
        { "enabled", HttpStatusCode.Conflict },
        { "locked", HttpStatusCode.TooManyRequests },
    };

    [Theory]
    [MemberData(nameof(ConfirmFailures))]
    public async Task Confirm_Failure_MapsToTheDesignedStatus(string failure, HttpStatusCode expected)
    {
        var error = Failure(failure);
        _host.Confirm.Answer = Result.Failure<TwoFactorRecoveryCodesResponse>(error);
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/enrollment/confirm", new TwoFactorCodeRequest("000000"));

        response.StatusCode.Should().Be(expected);
        (await ProblemCodeAsync(response)).Should().Be(error.Code);
    }

    // -- Regenerate recovery codes --
    [Fact]
    public async Task Regenerate_DispatchesTheCode_AndReturnsTheNewCodesUncached()
    {
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/recovery-codes", new TwoFactorCodeRequest("111111"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesResponse>(Ct))!.RecoveryCodes.Should().Equal("fresh-1", "fresh-2", "fresh-3");
        var command = _host.Regenerate.Received.Should().ContainSingle().Subject;
        command.UserId.Should().Be(TwoFactorControllerTestHost.UserId);
        command.Request.Code.Should().Be("111111");
        AssertNoStore(response);
    }

    public static TheoryData<string, HttpStatusCode> CodeGuardedFailures => new()
    {
        { "invalid", HttpStatusCode.Unauthorized },
        { "required", HttpStatusCode.Unauthorized },
        { "not-enrolled", HttpStatusCode.Conflict },
        { "locked", HttpStatusCode.TooManyRequests },
    };

    [Theory]
    [MemberData(nameof(CodeGuardedFailures))]
    public async Task Regenerate_Failure_MapsToTheDesignedStatus(string failure, HttpStatusCode expected)
    {
        var error = Failure(failure);
        _host.Regenerate.Answer = Result.Failure<TwoFactorRecoveryCodesResponse>(error);
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/recovery-codes", new TwoFactorCodeRequest("000000"));

        response.StatusCode.Should().Be(expected);
        (await ProblemCodeAsync(response)).Should().Be(error.Code);
    }

    // -- Disable --
    [Fact]
    public async Task Disable_DispatchesTheCode_AndAnswers204()
    {
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/disable", new TwoFactorCodeRequest("222222"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var command = _host.Disable.Received.Should().ContainSingle().Subject;
        command.UserId.Should().Be(TwoFactorControllerTestHost.UserId);
        command.Request.Code.Should().Be("222222");
    }

    [Theory]
    [MemberData(nameof(CodeGuardedFailures))]
    public async Task Disable_Failure_MapsToTheDesignedStatus(string failure, HttpStatusCode expected)
    {
        var error = Failure(failure);
        _host.Disable.Answer = Result.Failure(error);
        using var client = _host.SignedIn(Fresh);

        using var response = await PostAsync(client, "/disable", new TwoFactorCodeRequest("000000"));

        response.StatusCode.Should().Be(expected);
        (await ProblemCodeAsync(response)).Should().Be(error.Code);
    }

    // The code itself proves possession for these two, so a sign-in of any age may use them.
    [Theory]
    [InlineData("/recovery-codes", HttpStatusCode.OK)]
    [InlineData("/disable", HttpStatusCode.NoContent)]
    public async Task RegenerateAndDisable_DoNotRequireARecentSignIn(string action, HttpStatusCode expected)
    {
        using var client = _host.SignedIn(Stale);

        using var response = await PostAsync(client, action, new TwoFactorCodeRequest("333333"));

        response.StatusCode.Should().Be(expected);
    }

    private static Error Failure(string name) => name switch
    {
        "invalid" => TwoFactorErrors.TwoFactorInvalid(),
        "required" => TwoFactorErrors.TwoFactorRequired(),
        "not-enrolled" => TwoFactorErrors.TwoFactorNotEnrolled(),
        "missing" => TwoFactorErrors.TwoFactorEnrollmentMissing(),
        "enabled" => Error.Conflict("Authentication.TwoFactorAlreadyEnabled", "Already on."),
        "locked" => Error.TooManyRequests("Auth.TooManyAttempts", "Too many attempts."),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static List<MethodInfo> ActionMethods()
    {
        var methods = typeof(TwoFactorControllerBase<,,,>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        return [.. methods.Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())];
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl.NoStore.Should().BeTrue("a secret or a recovery code must never sit in a cache");
        response.Headers.Pragma.Should().Contain(p => p.Name == "no-cache");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string action, TwoFactorCodeRequest? body) =>
        body is { } request
            ? await client.PostAsJsonAsync(TwoFactorControllerTestHost.Route + action, request, Ct)
            : await client.PostAsync(new Uri(TwoFactorControllerTestHost.Route + action, UriKind.Relative), content: null, Ct);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new TwoFactorCodeRequest("123456"));
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
            ? errors[0].GetProperty("code").GetString()
            : null;
    }

    private void NothingDispatched()
    {
        _host.Begin.Received.Should().BeEmpty();
        _host.Confirm.Received.Should().BeEmpty();
        _host.Disable.Received.Should().BeEmpty();
        _host.Regenerate.Received.Should().BeEmpty();
    }
}
