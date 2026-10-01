using System.Globalization;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// bUnit tests for the signed-in devices page: the loaded/empty/failed render states, the two
/// deliberately different revoke paths (one row versus the whole account), and the busy lockout that
/// keeps a second click from racing the reload.
/// <para>
/// The row for the current device is the interesting one: it must carry the "this device" marker and
/// must NOT offer a per-row sign-out, because revoking it from a row would leave the app running on
/// a dead session until the access token expired.
/// </para>
/// </summary>
public sealed class SessionsTests : BunitTestBase
{
    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private const string FirefoxOnMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:127.0) Gecko/20100101 Firefox/127.0";

    private const string CurrentDeviceButtonSelector = "button[aria-label=\"Sign out of Chrome on Windows\"]";
    private const string OtherDeviceButtonSelector = "button[aria-label=\"Sign out of Firefox on macOS\"]";
    private const string SignOutEverywhereSelector =
        "button[aria-label=\"Sign out of every device, including this one\"]";

    private static readonly Guid CurrentSessionId =
        new(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11);
    private static readonly Guid OtherSessionId =
        new(0x22222222, 0x2222, 0x2222, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22);
    private static readonly DateTime CreatedAt = new(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpiresAt = new(2026, 9, 1, 9, 30, 0, DateTimeKind.Utc);

    private readonly Mock<IAuthUIService> _auth = new();
    private readonly Mock<IToastService> _toast = new();
    private readonly Mock<IAppDialogService> _dialogs = new();

    public SessionsTests()
    {
        Services.AddSingleton(_auth.Object);
        // Registered after the base class's default facade so this wins, and the page's toasts can
        // be counted without rendering a snackbar provider.
        Services.AddSingleton<IToastService>(_toast.Object);
        // Both revoke paths ask first. The double confirms by default so the revoke tests below
        // exercise the action itself; the confirm tests decline explicitly.
        Services.AddSingleton<IAppDialogService>(_dialogs.Object);
        _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _auth.Setup(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Loaded(CurrentDevice(), OtherDevice()));
        _auth.Setup(a => a.RevokeSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _auth.Setup(a => a.RevokeAllSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    private static RefreshSessionSummaryResponse CurrentDevice() =>
        new(CurrentSessionId, CreatedAt, ExpiresAt, "203.0.113.7", ChromeOnWindows, IsCurrent: true);

    private static RefreshSessionSummaryResponse OtherDevice(string? ipAddress = "198.51.100.4", string? userAgent = FirefoxOnMac) =>
        new(OtherSessionId, CreatedAt, ExpiresAt, ipAddress, userAgent, IsCurrent: false);

    private static Result<IReadOnlyList<RefreshSessionSummaryResponse>> Loaded(
        params RefreshSessionSummaryResponse[] sessions) =>
        Result.Success<IReadOnlyList<RefreshSessionSummaryResponse>>(sessions);

    // The message is not a resource key, so the localizer passes it through and the rendered text can
    // be asserted exactly.
    private static Result<IReadOnlyList<RefreshSessionSummaryResponse>> LoadFailure() =>
        Result.Failure<IReadOnlyList<RefreshSessionSummaryResponse>>(
            Error.Failure("Auth.Sessions.LoadFailed", "Your devices could not be loaded."));

    private IRenderedComponent<Sessions> RenderSessions() =>
        RenderAs<Sessions>(TestPrincipal.AuthenticatedUser(), _ => { });

    /// <summary>MudBlazor renders the boolean <c>Disabled</c> parameter as a bare <c>disabled</c> attribute.</summary>
    private static bool IsDisabled(IElement element) => element.HasAttribute("disabled");

    private static string LocalInstant(DateTime utcInstant) =>
        DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    // ==================== Loaded state ====================
    [Fact]
    public void WhenSessionsLoad_RendersOneRowPerDevice()
    {
        var cut = RenderSessions();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.Markup.Should().Contain("Chrome on Windows");
        cut.Markup.Should().Contain("Firefox on macOS");
        cut.Markup.Should().Contain("203.0.113.7");
        cut.Markup.Should().Contain("198.51.100.4");
    }

    [Fact]
    public void WhenSessionsLoad_RendersTheSignedInAndExpiryInstantsInLocalTime()
    {
        var cut = RenderSessions();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.Markup.Should().Contain(LocalInstant(CreatedAt));
        cut.Markup.Should().Contain(LocalInstant(ExpiresAt));
    }

    [Fact]
    public void WhenASessionHasNoRecordedIpAddress_RendersTheNotRecordedWording()
    {
        _auth.Setup(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Loaded(CurrentDevice(), OtherDevice(ipAddress: null)));

        var cut = RenderSessions();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Not recorded"));
    }

    [Fact]
    public void WhenAUserAgentIsUnrecognizable_RendersTheUnknownDeviceWording()
    {
        _auth.Setup(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Loaded(CurrentDevice(), OtherDevice(userAgent: null)));

        var cut = RenderSessions();

        // The raw header is never shown: it is neither readable nor localizable.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Unrecognized device"));
    }

    [Fact]
    public void TheCurrentDeviceRow_CarriesTheThisDeviceChipAndOffersNoRevokeButton()
    {
        var cut = RenderSessions();

        cut.WaitForAssertion(() => cut.FindAll(".mmca-current-device").Should().ContainSingle());
        cut.FindAll(CurrentDeviceButtonSelector).Should().BeEmpty();
        cut.Markup.Should().Contain("This device");
        cut.Markup.Should().Contain("In use now");
    }

    [Fact]
    public void EveryOtherRow_RendersAnEnabledRevokeButton()
    {
        var cut = RenderSessions();

        cut.WaitForAssertion(() => cut.FindAll(OtherDeviceButtonSelector).Should().ContainSingle());
        IsDisabled(cut.Find(OtherDeviceButtonSelector)).Should().BeFalse();
        IsDisabled(cut.Find(SignOutEverywhereSelector)).Should().BeFalse();
    }

    // ==================== Empty and failed states ====================
    [Fact]
    public void WhenNoSessionsComeBack_RendersTheEmptyStateInsteadOfATable()
    {
        _auth.Setup(a => a.GetSessionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Loaded());

        var cut = RenderSessions();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No other devices are signed in."));
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public void WhenTheLoadFails_RendersTheErrorSummaryAndARetryButton()
    {
        _auth.Setup(a => a.GetSessionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(LoadFailure());

        var cut = RenderSessions();

        // Inline, not a snackbar: an empty table and a failed load look identical once a toast expires.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Your devices could not be loaded."));
        cut.FindAll(".mud-alert").Should().ContainSingle();
        cut.Markup.Should().Contain("Try again");
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public void WhenTheLoadFails_ClickingRetryAsksTheServiceAgain()
    {
        _auth.SetupSequence(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoadFailure())
            .ReturnsAsync(Loaded(CurrentDevice(), OtherDevice()));

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Try again"));

        cut.ClickButtonByText("Try again");

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        _auth.Verify(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        cut.Markup.Should().NotContain("Your devices could not be loaded.");
    }

    [Fact]
    public void WhenAReloadFails_TheStaleDeviceListIsNotLeftOnScreen()
    {
        _auth.SetupSequence(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Loaded(CurrentDevice(), OtherDevice()))
            .ReturnsAsync(LoadFailure());

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        // A failed read must not leave rows a user could act on.
        cut.WaitForAssertion(() => cut.FindAll("table").Should().BeEmpty());
        cut.Markup.Should().Contain("Your devices could not be loaded.");
    }

    // ==================== Per-device revoke ====================
    [Fact]
    public void ClickingARowsRevokeButton_RevokesThatSessionThenReloadsAndConfirms()
    {
        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        cut.WaitForAssertion(() =>
            _auth.Verify(a => a.RevokeSessionAsync(OtherSessionId, It.IsAny<CancellationToken>()), Times.Once()));
        _auth.Verify(a => a.RevokeSessionAsync(CurrentSessionId, It.IsAny<CancellationToken>()), Times.Never());
        // Reloaded from the server rather than dropping the row locally: the server is the authority.
        _auth.Verify(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _toast.Verify(t => t.Success("That device has been signed out."), Times.Once());
    }

    [Fact]
    public void WhenARevokeReportsNotFound_SaysItWasAlreadySignedOutAndStillReloads()
    {
        _auth.Setup(a => a.RevokeSessionAsync(OtherSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFoundError("Auth.Session.NotFound", "No such session.")));

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        // The user's intent is satisfied, so this is not an error to shout about.
        cut.WaitForAssertion(() =>
            _toast.Verify(t => t.Info("That device was already signed out."), Times.Once()));
        _auth.Verify(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _toast.Verify(t => t.Show("That device was already signed out.", ToastSeverity.Error), Times.Never());
    }

    [Fact]
    public void WhenARevokeFailsForAnyOtherReason_ShowsTheErrorAndDoesNotReload()
    {
        _auth.Setup(a => a.RevokeSessionAsync(OtherSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("Auth.Session.RevokeFailed", "That device could not be signed out.")));

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        cut.WaitForAssertion(() =>
            _toast.Verify(t => t.Show("That device could not be signed out.", ToastSeverity.Error), Times.Once()));
        _auth.Verify(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()), Times.Once());
        _toast.Verify(t => t.Success("That device has been signed out."), Times.Never());
    }

    [Fact]
    public void WhileARevokeIsInFlight_EveryButtonIsDisabled()
    {
        var pending = new TaskCompletionSource<Result>();
        _auth.Setup(a => a.RevokeSessionAsync(OtherSessionId, It.IsAny<CancellationToken>()))
            .Returns(pending.Task);

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        cut.WaitForAssertion(() =>
            IsDisabled(cut.Find(OtherDeviceButtonSelector)).Should().BeTrue());
        IsDisabled(cut.Find(SignOutEverywhereSelector)).Should().BeTrue();

        pending.SetResult(Result.Success());

        cut.WaitForAssertion(() =>
            IsDisabled(cut.Find(OtherDeviceButtonSelector)).Should().BeFalse());
    }

    [Fact]
    public void ClickingARowsRevokeButton_AsksForConfirmationNamingTheDevice()
    {
        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        cut.WaitForAssertion(() => _dialogs.Verify(
            d => d.ConfirmAsync(
                "Sign out this device?",
                "Firefox on macOS will be signed out and will have to sign in again.",
                "Sign out",
                "Cancel"),
            Times.Once()));
    }

    [Fact]
    public void WhenARowsRevokeIsCancelled_NothingIsRevoked()
    {
        _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(OtherDeviceButtonSelector).Click();

        cut.WaitForAssertion(() => _dialogs.Verify(
            d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once()));
        _auth.Verify(a => a.RevokeSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _auth.Verify(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()), Times.Once());
        IsDisabled(cut.Find(OtherDeviceButtonSelector)).Should().BeFalse();
    }

    // ==================== Account-wide sign-out ====================
    [Fact]
    public void ClickingSignOutEverywhere_AsksForConfirmationFirst()
    {
        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(SignOutEverywhereSelector).Click();

        cut.WaitForAssertion(() => _dialogs.Verify(
            d => d.ConfirmAsync(
                "Sign out everywhere?",
                "Every device will be signed out, including this one, and you will have to sign in again.",
                "Sign out everywhere",
                "Cancel"),
            Times.Once()));
    }

    [Fact]
    public void WhenSignOutEverywhereIsCancelled_NothingIsRevokedAndThePageStays()
    {
        _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var navigation = Services.GetRequiredService<NavigationManager>();

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        var uriBefore = navigation.Uri;

        cut.Find(SignOutEverywhereSelector).Click();

        cut.WaitForAssertion(() => _dialogs.Verify(
            d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once()));
        _auth.Verify(a => a.RevokeAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Never());
        navigation.Uri.Should().Be(uriBefore);
    }

    [Fact]
    public void ClickingSignOutEverywhere_SignsOutThroughTheAuthServiceAndReturnsToLogin()
    {
        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(SignOutEverywhereSelector).Click();

        // One call does both halves: the account-wide server revoke AND (on success) the local
        // sign-out. M131: the reporting RevokeAllSessionsAsync, not the best-effort LogoutAsync.
        cut.WaitForAssertion(() => _auth.Verify(a => a.RevokeAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Once()));
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/login");
    }

    [Fact]
    public void ClickingSignOutEverywhere_DoesNotCallThePerDeviceRevoke()
    {
        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find(SignOutEverywhereSelector).Click();

        cut.WaitForAssertion(() => _auth.Verify(a => a.RevokeAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Once()));
        _auth.Verify(a => a.RevokeSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void WhenSignOutEverywhereFails_ShowsTheErrorAndStaysOnThePage()
    {
        // M131: the button promises every device is signed out, so a refused revoke must be said,
        // not papered over by the best-effort logout.
        _auth.Setup(a => a.RevokeAllSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("Auth.Sessions.RevokeAllFailed", "Your other devices could not be signed out.")));
        var navigation = Services.GetRequiredService<NavigationManager>();

        var cut = RenderSessions();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        var uriBefore = navigation.Uri;

        cut.Find(SignOutEverywhereSelector).Click();

        cut.WaitForAssertion(() =>
            _toast.Verify(t => t.Show("Your other devices could not be signed out.", ToastSeverity.Error), Times.Once()));
        navigation.Uri.Should().Be(uriBefore);
        _auth.Verify(a => a.LogoutAsync(), Times.Never());
    }
}
