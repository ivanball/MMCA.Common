using AwesomeAssertions;
using Bunit;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Culture;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Culture;

/// <summary>
/// <see cref="ViewerTimeZone"/> shows UTC instants on the viewer's browser clock: a UTC (or
/// unspecified, read as UTC) instant converts to the zone the browser reports, and whenever that zone
/// is not available (prerender, no JS, no answer, an unknown id) the conversion is UTC, never the
/// server's own zone.
/// </summary>
public sealed class ViewerTimeZoneTests : BunitTestBase
{
    private const string ModulePath = "./_content/MMCA.Common.UI/time-zone.js";

    // 12:00 UTC in January (New York on EST, UTC-5) and in July (EDT, UTC-4).
    private static readonly DateTime WinterNoonUtc = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SummerNoonUtc = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ABrowserZone_ConvertsUtcInstantsToThatZoneIncludingDaylightSaving()
    {
        await using var sut = CreateWithBrowserZone("America/New_York");

        (await sut.EnsureResolvedAsync()).Should().BeTrue("the zone moved off the UTC default, so the page must re-render");

        sut.ToViewerTime(WinterNoonUtc).Should().Be(new DateTime(2026, 1, 15, 7, 0, 0, DateTimeKind.Unspecified));
        sut.ToViewerTime(SummerNoonUtc).Should().Be(new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public async Task AnUnspecifiedKindInstant_IsReadAsUtc()
    {
        await using var sut = CreateWithBrowserZone("Asia/Tokyo");
        await sut.EnsureResolvedAsync();

        var unspecified = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified);

        sut.ToViewerTime(unspecified).Should().Be(new DateTime(2026, 1, 15, 21, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public async Task Format_RendersTheInstantOnTheViewersClock()
    {
        await using var sut = CreateWithBrowserZone("Asia/Tokyo");
        await sut.EnsureResolvedAsync();

        sut.Format(WinterNoonUtc, "yyyy-MM-dd HH:mm").Should().Be("2026-01-15 21:00");
    }

    [Fact]
    public async Task BeforeTheZoneIsRead_InstantsStayInUtc()
    {
        await using var sut = CreateWithBrowserZone("Asia/Tokyo");

        sut.Zone.Should().Be(TimeZoneInfo.Utc);
        sut.ToViewerTime(WinterNoonUtc).Should().Be(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public async Task Format_BeforeTheZoneIsRead_RendersAPlaceholderInsteadOfUtc()
    {
        // Prerender (and the first interactive render) cannot read the browser zone. Painting the UTC
        // time there and the viewer's time after hydration made every timestamp jump; the placeholder
        // stays put until the one value that will not change is known.
        await using var sut = CreateWithBrowserZone("Asia/Tokyo");

        sut.Format(WinterNoonUtc, "HH:mm").Should().BeEmpty();

        (await sut.EnsureResolvedAsync()).Should().BeTrue();
        sut.Format(WinterNoonUtc, "HH:mm").Should().Be("21:00");
    }

    [Fact]
    public async Task EnsureResolvedAsync_WhenTheBrowserIsOnUtc_StillAsksForARerender()
    {
        // The zone does not move, but the rendered text does (placeholder to time), so a page that
        // re-renders only on true must still get it.
        await using var sut = CreateWithBrowserZone("UTC");

        (await sut.EnsureResolvedAsync()).Should().BeTrue();
        sut.Format(WinterNoonUtc, "HH:mm").Should().Be("12:00");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public async Task AMissingOrUnknownZone_FallsBackToUtc(string? zoneId)
    {
        await using var sut = CreateWithBrowserZone(zoneId);

        (await sut.EnsureResolvedAsync()).Should().BeTrue("the zone stays UTC, but the formatted text moves off the placeholder");

        sut.IsResolved.Should().BeTrue();
        sut.Zone.Should().Be(TimeZoneInfo.Utc);
        sut.Format(WinterNoonUtc, "HH:mm").Should().Be("12:00");
    }

    [Fact]
    public async Task WhenJsInteropIsUnavailable_FallsBackToUtcAndRetriesLater()
    {
        // Prerender (and a host without JS): the runtime throws InvalidOperationException.
        var unavailable = new Mock<IJSRuntime>();
        unavailable
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(new InvalidOperationException("JavaScript interop calls cannot be issued at this time."));
        await using var sut = new ViewerTimeZone(unavailable.Object);

        (await sut.EnsureResolvedAsync()).Should().BeFalse();

        sut.IsResolved.Should().BeFalse("a later call, once interactive, must still be able to read the zone");
        sut.Zone.Should().Be(TimeZoneInfo.Utc);
        sut.ToViewerTime(SummerNoonUtc).Should().Be(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public async Task WhenTheBrowserCallFails_FallsBackToUtc()
    {
        JSInterop.SetupModule(ModulePath).Setup<string?>("getTimeZone").SetException(new JSException("Intl unavailable"));
        await using var sut = new ViewerTimeZone(JSInterop.JSRuntime);

        (await sut.EnsureResolvedAsync()).Should().BeTrue("the browser will not do better on a retry, so UTC is now the final answer to render");

        sut.Zone.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task TheBrowserIsAskedOnlyOnce()
    {
        await using var sut = CreateWithBrowserZone("Asia/Tokyo");

        await sut.EnsureResolvedAsync();
        (await sut.EnsureResolvedAsync()).Should().BeFalse();

        JSInterop.Invocations.Count(i => i.Identifier == "getTimeZone").Should().Be(1);
    }

    private ViewerTimeZone CreateWithBrowserZone(string? zoneId)
    {
        JSInterop.SetupModule(ModulePath).Setup<string?>("getTimeZone").SetResult(zoneId);
        return new ViewerTimeZone(JSInterop.JSRuntime);
    }
}
