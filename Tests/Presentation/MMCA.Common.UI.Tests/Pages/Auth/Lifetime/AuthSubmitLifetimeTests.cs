using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.Lifetime;

/// <summary>
/// The sign-in and registration submits run under the page lifetime: <c>/login</c> and
/// <c>/register</c> pass a token their <c>Dispose</c> cancels, so a call still in flight when the
/// user navigates away (or an InteractiveAuto render-mode transition tears the page down) stops
/// instead of completing into a component that is gone. A call that already completed is handled
/// exactly as before.
/// </summary>
public sealed class AuthSubmitLifetimeTests : BunitTestBase
{
    private readonly Mock<IAuthUIService> _auth = new();
    private readonly Mock<IUserPreferenceReader> _preferences = new();

    public AuthSubmitLifetimeTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(_preferences.Object);
    }

    [Fact]
    public void Login_PassesALiveCancellableToken_NotCancellationTokenNone()
    {
        var pending = new TaskCompletionSource<Result<AuthenticationResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CaptureLoginToken(pending.Task);

        var cut = RenderUnderTest<Login>(_ => { });
        SubmitLogin(cut);

        cut.WaitForAssertion(() => token().Should().NotBeNull("the submit must have reached the auth service"));
        token()!.Value.Should().NotBe(CancellationToken.None);
        token()!.Value.CanBeCanceled.Should().BeTrue("the page lifetime must be able to cancel the call");
        token()!.Value.IsCancellationRequested.Should().BeFalse("the page is still on screen");
    }

    [Fact]
    public async Task Login_DisposedWhileTheCallIsPending_CancelsThatToken()
    {
        var pending = new TaskCompletionSource<Result<AuthenticationResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CaptureLoginToken(pending.Task);

        var cut = RenderUnderTest<Login>(_ => { });
        SubmitLogin(cut);
        await cut.WaitForAssertionAsync(() => token().Should().NotBeNull());

        await DisposeComponentsAsync();

        token()!.Value.IsCancellationRequested.Should().BeTrue("disposing the page cancels the in-flight sign-in");

        // The service observes the token and gives up; the disposed page must swallow that quietly.
        pending.SetCanceled(token()!.Value);
        await Task.Yield();
    }

    [Fact]
    public void Login_ACompletedSuccess_StillNavigatesToTheReturnUrl()
    {
        _preferences
            .Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserPreferences(Culture: null, Theme: null));
        _auth
            .Setup(x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Signed());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/login?returnUrl=" + Uri.EscapeDataString("/orders"));

        var cut = RenderUnderTest<Login>(_ => { });
        SubmitLogin(cut);

        cut.WaitForAssertion(() => new Uri(navigation.Uri).AbsolutePath.Should().Be("/orders"));
        cut.FindAll("[role='alert']").Should().BeEmpty("a completed sign-in has nothing to report");
    }

    [Fact]
    public void Login_ACompletedFailure_StillShowsTheRefusal()
    {
        _auth
            .Setup(x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidCredentials", "Invalid email or password.", "LoginAsync")));

        var cut = RenderUnderTest<Login>(_ => { });
        SubmitLogin(cut);

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Trim()
            .Should().Be("Invalid email or password."));
    }

    [Fact]
    public void Register_PassesALiveCancellableToken_NotCancellationTokenNone()
    {
        var pending = new TaskCompletionSource<Result<AuthenticationResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CaptureRegisterToken(pending.Task);

        var cut = RenderUnderTest<Register>(_ => { });
        SubmitRegister(cut);

        cut.WaitForAssertion(() => token().Should().NotBeNull("the submit must have reached the auth service"));
        token()!.Value.Should().NotBe(CancellationToken.None);
        token()!.Value.CanBeCanceled.Should().BeTrue("the page lifetime must be able to cancel the call");
        token()!.Value.IsCancellationRequested.Should().BeFalse("the page is still on screen");
    }

    [Fact]
    public async Task Register_DisposedWhileTheCallIsPending_CancelsThatToken()
    {
        var pending = new TaskCompletionSource<Result<AuthenticationResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CaptureRegisterToken(pending.Task);

        var cut = RenderUnderTest<Register>(_ => { });
        SubmitRegister(cut);
        await cut.WaitForAssertionAsync(() => token().Should().NotBeNull());

        await DisposeComponentsAsync();

        token()!.Value.IsCancellationRequested.Should().BeTrue("disposing the page cancels the in-flight registration");

        pending.SetCanceled(token()!.Value);
        await Task.Yield();
    }

    [Fact]
    public void Register_ACompletedSuccess_StillNavigatesHome()
    {
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Signed());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/register");

        var cut = RenderUnderTest<Register>(_ => { });
        SubmitRegister(cut);

        cut.WaitForAssertion(() => new Uri(navigation.Uri).AbsolutePath.Should().Be("/"));
        cut.FindAll(".mud-alert").Should().BeEmpty("a completed registration has nothing to report");
    }

    [Fact]
    public void Register_ACompletedFailure_StillShowsTheServersWording()
    {
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Conflict("Auth.Register.EmailTaken", "That email address is already registered.")));

        var cut = RenderUnderTest<Register>(_ => { });
        SubmitRegister(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("That email address is already registered."));
    }

    private static Result<AuthenticationResponse> Signed()
        => Result.Success(new AuthenticationResponse(
            "access-token",
            "refresh-token",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)));

    private static void SubmitLogin(IRenderedComponent<Login> cut)
    {
        cut.Find("input[autocomplete='email']").Input("ada@example.com");
        cut.Find("input[autocomplete='current-password']").Input("Str0ng!Passw0rd");
        cut.Find("form").Submit();
    }

    private static void SubmitRegister(IRenderedComponent<Register> cut)
    {
        cut.Find("input[autocomplete='given-name']").Input("Ada");
        cut.Find("input[autocomplete='family-name']").Input("Lovelace");
        cut.Find("input[autocomplete='email']").Input("ada@example.com");
        cut.FindAll("input[autocomplete='new-password']")[0].Input("Str0ng!Passw0rd");
        cut.FindAll("input[autocomplete='new-password']")[1].Input("Str0ng!Passw0rd");
        cut.ClickButtonByText("Create Account");
    }

    private Func<CancellationToken?> CaptureLoginToken(Task<Result<AuthenticationResponse>> response)
    {
        CancellationToken? captured = null;
        _auth
            .Setup(x => x.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LoginRequest, CancellationToken>((_, ct) => captured = ct)
            .Returns(response);
        return () => captured;
    }

    private Func<CancellationToken?> CaptureRegisterToken(Task<Result<AuthenticationResponse>> response)
    {
        CancellationToken? captured = null;
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RegisterRequest, CancellationToken>((_, ct) => captured = ct)
            .Returns(response);
        return () => captured;
    }
}
