using AwesomeAssertions;
using Bunit;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Components.Auth;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// bUnit tests for <see cref="SignInHistoryTable"/> (T13): one row per sign-in with the shared device
/// label, a "this sign-in" marker on the caller's own, and a status cell that names how each one ended.
/// </summary>
public sealed class SignInHistoryTableTests : BunitTestBase
{
    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly DateTime StartedAt = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static SignInSummaryResponse SignIn(string? endReason = null, bool isCurrent = false, string? ipAddress = "203.0.113.7") =>
        new(
            Guid.NewGuid(),
            StartedAt,
            StartedAt.AddHours(5),
            ipAddress,
            ChromeOnWindows,
            endReason is null ? null : StartedAt.AddHours(6),
            endReason,
            isCurrent);

    private IRenderedComponent<SignInHistoryTable> RenderTable(params SignInSummaryResponse[] signIns) =>
        Render<SignInHistoryTable>(p => p.Add(c => c.SignIns, signIns));

    [Fact]
    public void RendersOneRowPerSignIn_WithTheSharedDeviceLabelAndFiveColumns()
    {
        var cut = RenderTable(SignIn(), SignIn(SignInEndReasons.SignedOut));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.FindAll("thead th").Should().HaveCount(5);
        cut.Markup.Should().Contain("Chrome on Windows");
    }

    [Theory]
    [InlineData(null, "Active")]
    [InlineData(SignInEndReasons.SignedOut, "Signed out")]
    [InlineData(SignInEndReasons.ReuseDetected, "Ended: suspicious token reuse")]
    [InlineData(SignInEndReasons.SessionCapExceeded, "Ended: too many devices")]
    [InlineData(SignInEndReasons.AccountDeleted, "Ended: account deleted")]
    [InlineData(SignInEndReasons.Expired, "Expired")]
    [InlineData("SomethingNewer", "Ended")]
    public void TheStatusCell_NamesHowTheSignInEnded(string? endReason, string expected)
    {
        var cut = RenderTable(SignIn(endReason));

        cut.WaitForAssertion(() => cut.Find(".mmca-sign-in-status").TextContent.Trim().Should().Be(expected));
    }

    [Fact]
    public void OnlyTheCurrentSignIn_CarriesTheThisSignInMarker()
    {
        var cut = RenderTable(SignIn(isCurrent: true), SignIn(SignInEndReasons.Expired));

        cut.WaitForAssertion(() => cut.FindAll(".mmca-current-sign-in").Should().ContainSingle()
            .Which.TextContent.Should().Contain("This sign-in"));
    }

    [Fact]
    public void ASignInWithNoRecordedIpAddress_RendersTheNotRecordedWording()
    {
        var cut = RenderTable(SignIn(ipAddress: null));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Not recorded"));
    }

    [Fact]
    public void WithNoSignIns_RendersTheEmptyState()
    {
        var cut = RenderTable();

        cut.Markup.Should().Contain("No sign-ins recorded.");
        cut.FindAll("table").Should().BeEmpty();
    }
}
