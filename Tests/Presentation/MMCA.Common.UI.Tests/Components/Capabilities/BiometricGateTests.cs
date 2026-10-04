using System.Net;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Caching;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Covers <see cref="BiometricGate"/> (ADR-042 Wave 4), the app-lock overlay: inert unless the
/// opt-in device preference (<see cref="DevicePreferenceKeys.AppLockEnabled"/>) is set AND stored
/// tokens exist; on activation it prompts immediately and unlocks on success; a declined prompt
/// keeps the overlay up with retry (Unlock) and escape (Sign out) actions, and sign-out clears the
/// tokens and navigates to login instead of falling through to the signed-in session.
/// </summary>
public sealed class BiometricGateTests : BunitTestBase
{
    private readonly FakeBiometricAuthenticator _biometrics = new();
    private readonly FakeDevicePreferences _preferences = new();
    private readonly StubTokenStorageService _tokenStorage = new();
    private readonly FakeTimeProvider _time = new();
    private readonly AppLifecycleNotifier _lifecycle;

    public BiometricGateTests()
    {
        _lifecycle = new AppLifecycleNotifier(_time);
        Services.AddSingleton<IBiometricAuthenticator>(_biometrics);
        Services.AddSingleton<IDevicePreferences>(_preferences);
        Services.AddSingleton<MMCA.Common.UI.Services.Auth.Tokens.ITokenStorageService>(_tokenStorage);
        Services.AddSingleton<IAppLifecycleNotifier>(_lifecycle);

        // The gate signs out through the app's logout; this stand-in performs that logout's
        // token clear. The A-22 test below replaces it with the real AuthUIService.
        var auth = new Mock<IAuthUIService>();
        auth.Setup(a => a.LogoutAsync()).Returns(() => _tokenStorage.ClearTokensAsync());
        Services.AddSingleton(auth.Object);
    }

    // ── Re-lock on resume (SEC-ADC-67) ──
    [Fact]
    public async Task WhenTheAppReturnsFromABackgroundLongerThanTheThreshold_TheGateReArms()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = true;

        var cut = RenderUnderTest<BiometricGate>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty("the owner verified at start-up"));
        _biometrics.NextResult = false;

        _lifecycle.NotifyEnteredBackground();
        _time.Advance(TimeSpan.FromMinutes(5));
        _lifecycle.NotifyResumed();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(
            "mud-overlay",
            "the render tree survives a background cycle, so the gate has to engage again"));
        _biometrics.PromptReasons.Should().HaveCount(2, "resuming re-prompts the owner");
    }

    [Fact]
    public async Task WhenTheAppReturnsAlmostImmediately_TheGateStaysOpen()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = true;

        var cut = RenderUnderTest<BiometricGate>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty());

        _lifecycle.NotifyEnteredBackground();
        _time.Advance(TimeSpan.FromSeconds(2));
        _lifecycle.NotifyResumed();

        cut.Markup.Trim().Should().BeEmpty("a glance at a notification is not a hand-off of the device");
        _biometrics.PromptReasons.Should().ContainSingle();
    }

    // -- A failing precondition read (L72) --
    [Fact]
    public async Task WhenTheStoredSessionCannotBeRead_AfterOptIn_TheGateLocks()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = false;
        var unreadable = new Mock<MMCA.Common.UI.Services.Auth.Tokens.ITokenStorageService>();
        unreadable.Setup(t => t.GetRefreshTokenAsync()).ThrowsAsync(new InvalidOperationException("secure storage unavailable"));
        Services.AddSingleton(unreadable.Object);

        var cut = RenderUnderTest<BiometricGate>(_ => { });

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(
            "mud-overlay",
            "the owner opted in, so an unknown session state must fail closed"));
        _biometrics.PromptReasons.Should().ContainSingle();
    }

    [Fact]
    public async Task WhenThePreferenceCannotBeRead_TheGateStaysOpenAndLogs()
    {
        _preferences.GetThrows = () => new InvalidOperationException("preferences unavailable");

        var cut = RenderUnderTest<BiometricGate>(_ => { });

        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty(
            "nothing says the owner opted in, and forcing a prompt could strand a device with no biometrics"));
        _biometrics.PromptReasons.Should().BeEmpty();
    }

    private Task EnableAppLockAsync() =>

        _preferences.SetAsync(DevicePreferenceKeys.AppLockEnabled, true);

    // ── Inert states ──
    [Fact]
    public void WhenAppLockIsNotOptedIn_RendersNothingAndNeverPrompts()
    {
        DevicePreferenceKeys.AppLockEnabled.Should().Be("applock.enabled");

        var cut = RenderUnderTest<BiometricGate>(_ => { });

        cut.Markup.Trim().Should().BeEmpty();
        _biometrics.PromptReasons.Should().BeEmpty("the gate is strictly opt-in");
    }

    [Fact]
    public async Task WhenAppLockIsOnButNoSessionIsStored_RendersNothingAndNeverPrompts()
    {
        await EnableAppLockAsync();
        await _tokenStorage.ClearTokensAsync();

        var cut = RenderUnderTest<BiometricGate>(_ => { });

        cut.Markup.Trim().Should().BeEmpty("there is nothing to protect without a stored session");
        _biometrics.PromptReasons.Should().BeEmpty();
    }

    // ── Immediate prompt on activation ──
    [Fact]
    public async Task WhenActivated_PromptsImmediatelyAndUnlocksOnSuccess()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = true;

        var cut = RenderUnderTest<BiometricGate>(_ => { });

        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty("verification succeeded"));
        _biometrics.PromptReasons.Should().ContainSingle()
            .Which.Should().Be("Confirm it is you to continue", "the platform prompt shows the localized reason");
    }

    [Fact]
    public async Task WhenTheOwnerDeclines_StaysLockedWithUnlockAndSignOutActions()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = false;

        var cut = RenderUnderTest<BiometricGate>(_ => { });

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Markup.Should().Contain("Unlock the app");
            cut.Markup.Should().Contain("Unlock");
            cut.Markup.Should().Contain("Sign out instead");
        });
        _biometrics.PromptReasons.Should().ContainSingle("declining must never fall through to the session");
    }

    // ── Retry and escape paths ──
    [Fact]
    public async Task ClickingUnlock_RetriesThePrompt_AndHidesTheOverlayOnSuccess()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = false;
        var cut = RenderUnderTest<BiometricGate>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Unlock the app"));

        _biometrics.NextResult = true;
        await cut.FindAll("button")
            .Single(b => string.Equals(b.TextContent.Trim(), "Unlock", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty());
        _biometrics.PromptReasons.Should().HaveCount(2, "the initial prompt plus the retry");
    }

    [Fact]
    public async Task ClickingSignOut_ClearsTheStoredTokensAndNavigatesToLogin()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = false;
        var cut = RenderUnderTest<BiometricGate>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Sign out instead"));

        await cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Sign out instead", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty());
        _tokenStorage.AccessToken.Should().BeNull("declining offers sign-out, which clears the session");
        _tokenStorage.RefreshToken.Should().BeNull();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/login");
    }

    // ── Sign-out goes through the app's own logout path (A-22) ──
    // Clearing the tokens alone leaves Blazor's auth state signed in (no NotifyUserLogout) and leaves
    // the previous session's read cache and offline snapshots on the device. The gate's escape must
    // run the same local sign-out the app's logout does. The real AuthUIService is registered, so
    // the test pins the user-visible outcome rather than which member the gate calls.
    [Fact]
    public async Task ClickingSignOut_SignsOutThroughTheAppLogoutPath_NotifyingAuthStateAndClearingCaches()
    {
        await EnableAppLockAsync();
        _biometrics.NextResult = false;
        var readCache = new Mock<IUiReadCache>();
        var localCache = new Mock<ILocalCacheStore>();
        var authStateProvider = new JwtAuthenticationStateProvider(_tokenStorage);
        var notifications = new List<Task<AuthenticationState>>();
        authStateProvider.AuthenticationStateChanged += notifications.Add;
        using var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent);
        Services.AddSingleton<IAuthUIService>(new AuthUIService(
            new StubHttpClientFactory(handler),
            _tokenStorage,
            Mock.Of<ITokenRefresher>(),
            authStateProvider,
            Mock.Of<IPushRegistrationService>(),
            readCache.Object,
            localCache.Object));
        var cut = RenderUnderTest<BiometricGate>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Sign out instead"));

        await cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Sign out instead", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Trim().Should().BeEmpty());
        notifications.Should().ContainSingle(
            "signing out from the lock screen must tell Blazor's auth state, exactly as the app's logout does");
        var state = await notifications[0];
        state.User.Identity!.IsAuthenticated.Should().BeFalse();
        readCache.Verify(c => c.Clear(), Times.Once, "the previous session's cached reads must not survive the sign-out");
        localCache.Verify(
            c => c.ClearAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "the previous session's offline snapshots must not survive the sign-out");
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/login");
    }

    // ── Fakes ──
    private sealed class FakeBiometricAuthenticator : IBiometricAuthenticator
    {
        public bool NextResult { get; set; }

        public List<string> PromptReasons { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken = default)
        {
            PromptReasons.Add(reason);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeDevicePreferences : IDevicePreferences
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public bool IsPersistent => true;

        /// <summary>When set, every read throws the exception it builds (a failing platform store).</summary>
        public Func<Exception>? GetThrows { get; set; }

        public Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
        {
            if (GetThrows is not null)
            {
                return Task.FromException<T>(GetThrows());
            }

            return Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : fallback);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
