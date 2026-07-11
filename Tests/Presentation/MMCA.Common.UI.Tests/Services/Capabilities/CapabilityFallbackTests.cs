using AwesomeAssertions;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Fallbacks;

namespace MMCA.Common.UI.Tests.Services.Capabilities;

/// <summary>
/// Covers the null/neutral capability defaults (ADR-042): every fallback must degrade —
/// report unsupported, succeed as a no-op, or return empty — and never throw, because these
/// are what shared components resolve on hosts without a native or browser override.
/// </summary>
public sealed class CapabilityFallbackTests
{
    [Fact]
    public async Task NullShareService_ReportsNotShared()
    {
        var sut = new NullShareService();

        (await sut.ShareLinkAsync("t", new Uri("https://example.com"))).Should().BeFalse();
        (await sut.ShareFileAsync("t", "/tmp/x.png", "image/png")).Should().BeFalse();
    }

    private static readonly int[] CancelIds = [1, 2];
    private static readonly string[] CachedDocument = ["a", "b"];

    [Fact]
    public async Task NullClipboardService_ReportsCopyFailure() =>
        (await new NullClipboardService().SetTextAsync("wifi-password")).Should().BeFalse();

    [Fact]
    public void NullHapticFeedbackService_IsUnsupportedAndSilent()
    {
        var sut = new NullHapticFeedbackService();

        sut.IsSupported.Should().BeFalse();
        var act = () =>
        {
            sut.Click();
            sut.LongPress();
            sut.Vibrate(TimeSpan.FromMilliseconds(50));
        };
        act.Should().NotThrow();
    }

    [Fact]
    public async Task NullGeolocationService_ReturnsNullWithoutPrompting()
    {
        var sut = new NullGeolocationService();

        sut.IsSupported.Should().BeFalse();
        (await sut.GetCurrentOrLastKnownAsync()).Should().BeNull();
    }

    [Fact]
    public async Task AlwaysOnlineConnectivity_IsOnlineAndInitializeIsANoOp()
    {
        var sut = new AlwaysOnlineConnectivityStatusService();

        sut.IsOnline.Should().BeTrue();
        await sut.InitializeAsync();
        sut.IsOnline.Should().BeTrue();
    }

    [Fact]
    public async Task NullLocalNotificationService_DeniesPermissionAndSwallowsScheduling()
    {
        var sut = new NullLocalNotificationService();

        sut.IsSupported.Should().BeFalse();
        (await sut.RequestPermissionAsync()).Should().BeFalse();
        var act = async () =>
        {
            await sut.ScheduleAsync(new LocalNotificationRequest(1, "t", "b", DateTimeOffset.UtcNow.AddHours(1), "/x"));
            await sut.CancelAsync(CancelIds);
            await sut.CancelAllAsync();
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InMemoryDevicePreferences_RoundTripsButIsNotPersistent()
    {
        var sut = new InMemoryDevicePreferences();

        sut.IsPersistent.Should().BeFalse();
        (await sut.GetAsync("reminders.leadMinutes", 15)).Should().Be(15);

        await sut.SetAsync("reminders.leadMinutes", 30);
        (await sut.GetAsync("reminders.leadMinutes", 15)).Should().Be(30);

        await sut.RemoveAsync("reminders.leadMinutes");
        (await sut.GetAsync("reminders.leadMinutes", 15)).Should().Be(15);
    }

    [Fact]
    public async Task NullLocalCacheStore_IsUnavailableAndReturnsDefaults()
    {
        var sut = new NullLocalCacheStore();

        sut.IsAvailable.Should().BeFalse();
        await sut.SetAsync("schedule", CachedDocument);
        (await sut.GetAsync<string[]>("schedule")).Should().BeNull();
    }

    [Fact]
    public async Task UnavailableExternalAuthBroker_KeepsWebFlow()
    {
        var sut = new UnavailableExternalAuthBroker();

        sut.IsAvailable.Should().BeFalse();
        (await sut.SignInAsync("google")).Should().BeFalse();
    }

    [Fact]
    public async Task RemainingNullFallbacks_DegradeSilently()
    {
        (await new NullMapNavigationService().OpenAddressAsync("100 Main St", "Venue")).Should().BeFalse();
        (await new NullBiometricAuthenticator().IsAvailableAsync()).Should().BeFalse();
        (await new NullBiometricAuthenticator().AuthenticateAsync("reason")).Should().BeFalse();
        (await new NullScreenshotService().CaptureToFileAsync()).Should().BeNull();
        (await new NullSpeechToTextService().ListenAsync(System.Globalization.CultureInfo.InvariantCulture, null))
            .Should().BeNull();
        new NullBatteryStatusService().IsEnergySaverOn.Should().BeFalse();
        new NullTextToSpeechService().IsSupported.Should().BeFalse();
        new NullExternalLinkService().InterceptsLinks.Should().BeFalse();
        var act = async () =>
        {
            await new NullTextToSpeechService().SpeakAsync("hello");
            await new NullTextToSpeechService().StopAsync();
            await new NullAccessibilityAnnouncer().AnnounceAsync("announcement");
            await new NullExternalLinkService().OpenAsync(new Uri("https://example.com"));
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void GeoPoint_ComputesGreatCircleDistance()
    {
        // Atlanta (33.749, -84.388) to Athens GA (33.951, -83.357): roughly 97 km.
        var atlanta = new GeoPoint(33.749, -84.388);
        var athens = new GeoPoint(33.951, -83.357);

        var distance = atlanta.DistanceKmTo(athens);

        distance.Should().BeInRange(90, 105);
        atlanta.DistanceKmTo(atlanta).Should().Be(0);
        athens.DistanceKmTo(atlanta).Should().BeApproximately(distance, 0.001);
    }
}
