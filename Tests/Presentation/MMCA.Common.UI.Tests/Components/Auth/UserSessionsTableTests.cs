using System.Globalization;
using AwesomeAssertions;
using Bunit;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Components.Auth;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// bUnit tests for <see cref="UserSessionsTable"/>, the administrator's read-only view of one account's
/// signed-in devices: one row per live session with the same device label the Sessions page uses
/// (native app included), the IP address or the existing "Not recorded" wording, and the Signed in and
/// Expires instants on the viewer's clock. View only: it offers no sign-out of any kind.
/// </summary>
public sealed class UserSessionsTableTests : BunitTestBase
{
    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private const string AtlDevConOnAndroid = "AtlDevCon/1.9.2 (Android 15; MmcaApp)";

    private static readonly DateTime NewerCreatedAt = new(2026, 8, 2, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerExpiresAt = new(2026, 9, 2, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OlderCreatedAt = new(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime OlderExpiresAt = new(2026, 9, 1, 9, 30, 0, DateTimeKind.Utc);

    private static RefreshSessionSummaryResponse AppSession(string? ipAddress = "198.51.100.4") =>
        new(Guid.NewGuid(), NewerCreatedAt, NewerExpiresAt, ipAddress, AtlDevConOnAndroid, IsCurrent: false);

    private static RefreshSessionSummaryResponse BrowserSession(string? userAgent = ChromeOnWindows) =>
        new(Guid.NewGuid(), OlderCreatedAt, OlderExpiresAt, "203.0.113.7", userAgent, IsCurrent: false);

    private IRenderedComponent<UserSessionsTable> RenderTable(params RefreshSessionSummaryResponse[] sessions) =>
        Render<UserSessionsTable>(p => p.Add(c => c.Sessions, sessions));

    private static string InZone(DateTime utcInstant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), zone)
            .ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Makes the browser report <paramref name="zoneId"/> as the viewer's time zone.</summary>
    private void ArrangeBrowserTimeZone(string? zoneId) =>
        JSInterop.SetupModule("./_content/MMCA.Common.UI/time-zone.js")
            .Setup<string?>("getTimeZone")
            .SetResult(zoneId);

    // == Rows ==
    [Fact]
    public void RendersOneRowPerSession_WithTheSharedDeviceLabel()
    {
        var cut = RenderTable(AppSession(), BrowserSession());

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.Markup.Should().Contain("AtlDevCon app on Android");
        cut.Markup.Should().Contain("Chrome on Windows");
    }

    [Fact]
    public void RendersTheIpAddressOfEachSession()
    {
        var cut = RenderTable(AppSession(), BrowserSession());

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.Markup.Should().Contain("198.51.100.4");
        cut.Markup.Should().Contain("203.0.113.7");
    }

    [Fact]
    public void ASessionWithNoRecordedIpAddress_RendersTheNotRecordedWording()
    {
        var cut = RenderTable(AppSession(ipAddress: null));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        cut.Markup.Should().Contain("Not recorded");
    }

    [Fact]
    public void ASessionWithNoUserAgent_RendersTheUnrecognizedDeviceWording()
    {
        var cut = RenderTable(BrowserSession(userAgent: null));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        cut.Markup.Should().Contain("Unrecognized device");
    }

    [Fact]
    public void RendersTheDeviceIpSignedInAndExpiresColumns()
    {
        var cut = RenderTable(AppSession());

        cut.WaitForAssertion(() => cut.FindAll("thead th").Should().HaveCount(4));
        var headers = cut.FindAll("thead th").Select(th => th.TextContent.Trim()).ToList();
        headers.Should().Equal("Device", "IP address", "Signed in", "Expires");
    }

    // == Times on the viewer's clock, like the Sessions page ==
    [Fact]
    public void RendersTheSignedInAndExpiryInstantsInTheViewersTimeZone()
    {
        ArrangeBrowserTimeZone("Asia/Tokyo");
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        var cut = RenderTable(AppSession());

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(InZone(NewerCreatedAt, tokyo)));
        cut.Markup.Should().Contain(InZone(NewerExpiresAt, tokyo));
    }

    [Fact]
    public void WhenTheBrowserReportsNoTimeZone_RendersTheInstantsInUtc()
    {
        ArrangeBrowserTimeZone(null);

        var cut = RenderTable(AppSession());

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        cut.Markup.Should().Contain(InZone(NewerCreatedAt, TimeZoneInfo.Utc));
        cut.Markup.Should().Contain(InZone(NewerExpiresAt, TimeZoneInfo.Utc));
    }

    // == Empty state ==
    [Fact]
    public void WithNoSessions_RendersTheNotSignedInEmptyStateInsteadOfATable()
    {
        var cut = RenderTable();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Not signed in on any device"));
        cut.FindAll("table").Should().BeEmpty();
    }

    // == View only ==
    [Fact]
    public void OffersNoSignOutButtons()
    {
        var cut = RenderTable(AppSession(), BrowserSession());

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.FindAll("button").Should().BeEmpty("the administrator's view is read-only: no per-device or account-wide sign-out");
        cut.Markup.Should().NotContain("Sign out");
        cut.FindAll(".mmca-current-device").Should().BeEmpty("an administrator is never on the viewed user's device");
    }
}
