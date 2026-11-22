using System.Net;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Tests.Infrastructure;

namespace MMCA.Common.UI.Tests.Services.Auth.TwoFactor;

/// <summary>
/// The account page's client for the two-factor endpoints (design 2.2.3): each operation reaches its
/// route under <c>auth/two-factor</c> with the expected verb and body, carries the signed-in bearer,
/// reads a success into its response and a refusal into the API's own coded error, and sends a POST
/// exactly once (a code is single-use, so a replay would only be refused).
/// </summary>
public sealed class TwoFactorUIServiceTests : IDisposable
{
    private const string Bearer = "two-factor-bearer";

    private StubHttpMessageHandler _handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NotFound);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _handler.Dispose();

    [Fact]
    public async Task GetStatusAsync_GetsTheStatusRoute_WithTheBearer_AndReadsTheStatus()
    {
        var sut = CreateSut(_ => Json(HttpStatusCode.OK, """{ "isEnabled": true, "recoveryCodesRemaining": 7 }"""));

        var result = await sut.GetStatusAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEnabled.Should().BeTrue();
        result.Value.RecoveryCodesRemaining.Should().Be(7);
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri!.AbsolutePath.Should().Be("/auth/two-factor");
        request.Authorization!.Parameter.Should().Be(Bearer);
    }

    [Fact]
    public async Task BeginEnrollmentAsync_PostsTheEnrollmentRoute_WithNoBody_AndReadsTheSetup()
    {
        var sut = CreateSut(_ => Json(
            HttpStatusCode.OK,
            """{ "sharedKey": "JBSWY3DPEHPK3PXP", "provisioningUri": "otpauth://totp/App:ada?secret=JBSWY3DPEHPK3PXP" }"""));

        var result = await sut.BeginEnrollmentAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.SharedKey.Should().Be("JBSWY3DPEHPK3PXP");
        result.Value.ProvisioningUri.Should().StartWith("otpauth://totp/");
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsolutePath.Should().Be("/auth/two-factor/enrollment");
        request.Body.Should().BeNullOrEmpty("starting an enrollment sends nothing but the bearer");
        request.Authorization!.Parameter.Should().Be(Bearer);
    }

    [Fact]
    public async Task ConfirmEnrollmentAsync_PostsTheTrimmedCode_ToTheConfirmRoute_AndReadsTheCodes()
    {
        var sut = CreateSut(_ => Json(HttpStatusCode.OK, """{ "recoveryCodes": [ "AAAA-1111", "BBBB-2222" ] }"""));

        var result = await sut.ConfirmEnrollmentAsync(" 123456 ", Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecoveryCodes.Should().Equal("AAAA-1111", "BBBB-2222");
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsolutePath.Should().Be("/auth/two-factor/enrollment/confirm");
        request.Body.Should().Contain("\"123456\"").And.NotContain(" 123456 ");
        request.Authorization!.Parameter.Should().Be(Bearer);
    }

    [Fact]
    public async Task RegenerateRecoveryCodesAsync_PostsTheCode_ToTheRecoveryCodesRoute()
    {
        var sut = CreateSut(_ => Json(HttpStatusCode.OK, """{ "recoveryCodes": [ "CCCC-3333" ] }"""));

        var result = await sut.RegenerateRecoveryCodesAsync("654321", Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecoveryCodes.Should().Equal("CCCC-3333");
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsolutePath.Should().Be("/auth/two-factor/recovery-codes");
        request.Body.Should().Contain("654321");
    }

    [Fact]
    public async Task DisableAsync_PostsTheCode_ToTheDisableRoute_AndTreatsNoContentAsSuccess()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.DisableAsync("111222", Ct);

        result.IsSuccess.Should().BeTrue();
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsolutePath.Should().Be("/auth/two-factor/disable");
        request.Body.Should().Contain("111222");
        request.Authorization!.Parameter.Should().Be(Bearer);
    }

    [Fact]
    public async Task ARefusedCode_ComesBackAsTheApisCodedError()
    {
        var sut = CreateSut(_ => Problem(HttpStatusCode.Unauthorized, AuthErrorCodes.TwoFactorInvalid, "The two-factor code is invalid."));

        var result = await sut.ConfirmEnrollmentAsync("000000", Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(AuthErrorCodes.TwoFactorInvalid, "the page maps the refusal by its code");
        error.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task TheRecentSignInRefusal_KeepsItsCode_SoThePageCanOfferTheSignInAgainStep()
    {
        var sut = CreateSut(_ => Problem(
            HttpStatusCode.Forbidden,
            "Authentication.TwoFactorRecentSignInRequired",
            "Sign in again before changing two-factor authentication."));

        var result = await sut.BeginEnrollmentAsync(Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Authentication.TwoFactorRecentSignInRequired");
    }

    [Theory]
    [InlineData("enrollment")]
    [InlineData("confirm")]
    [InlineData("regenerate")]
    [InlineData("disable")]
    public async Task APost_OnAServerError_IsReportedWithoutARetry(string operation)
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var failed = operation switch
        {
            "enrollment" => (await sut.BeginEnrollmentAsync(Ct)).IsFailure,
            "confirm" => (await sut.ConfirmEnrollmentAsync("123456", Ct)).IsFailure,
            "regenerate" => (await sut.RegenerateRecoveryCodesAsync("123456", Ct)).IsFailure,
            _ => (await sut.DisableAsync("123456", Ct)).IsFailure,
        };

        failed.Should().BeTrue();
        _handler.Requests.Should().ContainSingle("a code is single-use, so a POST is never replayed");
    }

    [Fact]
    public async Task TheRequestThatNeverReachesAServer_IsATransportFailure_NotAnException()
    {
        var sut = CreateSut(_ => throw new HttpRequestException("Connection refused"));

        var result = await sut.DisableAsync("123456", Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(MMCA.Common.UI.Services.Api.HttpResultExecutor.TransportErrorCode);
    }

    private TwoFactorUIService CreateSut(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _handler.Dispose();
        _handler = new StubHttpMessageHandler(responder);
        return new TwoFactorUIService(new StubHttpClientFactory(_handler), new StubTokenStorageService(Bearer));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string message) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{ "status": {{((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "errors": [ { "code": "{{code}}", "message": "{{message}}", "type": "{{(status == HttpStatusCode.Unauthorized ? "Unauthorized" : "Forbidden")}}" } ] }""",
                Encoding.UTF8,
                "application/problem+json"),
        };
}
