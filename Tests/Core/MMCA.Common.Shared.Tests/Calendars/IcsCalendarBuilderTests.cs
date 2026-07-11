using System.Text;
using AwesomeAssertions;
using MMCA.Common.Shared.Calendars;

namespace MMCA.Common.Shared.Tests.Calendars;

/// <summary>
/// Covers <see cref="IcsCalendarBuilder"/>: the VCALENDAR envelope, UTC timestamp rendering,
/// RFC 5545 TEXT escaping, optional-field omission, CRLF + 75-octet folding (including the
/// multi-byte no-split guarantee), ordering, and determinism.
/// </summary>
public sealed class IcsCalendarBuilderTests
{
    private const string ProductId = "-//MMCA//Tests//EN";

    private static readonly DateTimeOffset DtStamp = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private static IcsEvent CreateEvent(
        string uid = "session-42@test",
        string summary = "Opening Keynote",
        string? description = null,
        string? location = null) =>
        new(
            uid,
            summary,
            new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(-4)),
            description,
            location);

    [Fact]
    public void Build_EmitsCalendarEnvelopeWithCrlfLineEndings()
    {
        var ics = IcsCalendarBuilder.Build(ProductId, [CreateEvent()], DtStamp);

        ics.Should().StartWith("BEGIN:VCALENDAR\r\n");
        ics.Should().EndWith("END:VCALENDAR\r\n");
        ics.Should().Contain("VERSION:2.0\r\n");
        ics.Should().Contain("PRODID:-//MMCA//Tests//EN\r\n");
        ics.Should().Contain("CALSCALE:GREGORIAN\r\n");
        ics.Should().Contain("METHOD:PUBLISH\r\n");

        // Every terminator is the full CRLF pair — no bare LF anywhere.
        ics.Replace("\r\n", string.Empty, StringComparison.Ordinal)
            .Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void Build_ConvertsEventTimesToUtcZuluFormat()
    {
        // 09:00 at UTC-4 is 13:00Z — the offset must be applied, never dropped.
        var ics = IcsCalendarBuilder.Build(ProductId, [CreateEvent()], DtStamp);

        ics.Should().Contain("DTSTART:20261017T130000Z\r\n");
        ics.Should().Contain("DTEND:20261017T140000Z\r\n");
        ics.Should().Contain("DTSTAMP:20260710T120000Z\r\n");
    }

    [Fact]
    public void Build_EscapesRfc5545TextCharacters()
    {
        var ics = IcsCalendarBuilder.Build(
            ProductId,
            [CreateEvent(summary: "AI; ML, and\nback\\slashes")],
            DtStamp);

        ics.Should().Contain("SUMMARY:AI\\; ML\\, and\\nback\\\\slashes");
    }

    [Fact]
    public void Build_OmitsDescriptionAndLocationWhenAbsent()
    {
        var ics = IcsCalendarBuilder.Build(ProductId, [CreateEvent(description: null, location: "  ")], DtStamp);

        ics.Should().NotContain("DESCRIPTION:");
        ics.Should().NotContain("LOCATION:");
    }

    [Fact]
    public void Build_IncludesDescriptionAndLocationWhenPresent()
    {
        var ics = IcsCalendarBuilder.Build(
            ProductId,
            [CreateEvent(description: "Session details", location: "Room A - Floor 2")],
            DtStamp);

        ics.Should().Contain("DESCRIPTION:Session details\r\n");
        ics.Should().Contain("LOCATION:Room A - Floor 2\r\n");
    }

    [Fact]
    public void Build_FoldsLinesLongerThan75OctetsAndUnfoldingRoundTrips()
    {
        var longSummary = string.Concat(Enumerable.Repeat("Nine char", 30)); // 270 chars
        var ics = IcsCalendarBuilder.Build(ProductId, [CreateEvent(summary: longSummary)], DtStamp);

        foreach (var line in ics.Split("\r\n", StringSplitOptions.None))
        {
            Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        }

        // Unfolding (removing CRLF + single space) restores the logical line.
        var unfolded = ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        unfolded.Should().Contain("SUMMARY:" + longSummary);
    }

    [Fact]
    public void Build_NeverSplitsMultiByteCharactersWhenFolding()
    {
        // Spanish accented vowels are two UTF-8 octets each; a naive char-count fold would
        // eventually split one across a boundary and corrupt the file.
        var accented = string.Concat(Enumerable.Repeat("sesión más", 20));
        var ics = IcsCalendarBuilder.Build(ProductId, [CreateEvent(summary: accented)], DtStamp);

        foreach (var line in ics.Split("\r\n", StringSplitOptions.None))
        {
            Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        }

        var unfolded = ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        unfolded.Should().Contain("SUMMARY:" + accented);
    }

    [Fact]
    public void Build_RendersEventsInInputOrder()
    {
        var ics = IcsCalendarBuilder.Build(
            ProductId,
            [CreateEvent(uid: "first@test", summary: "First"), CreateEvent(uid: "second@test", summary: "Second")],
            DtStamp);

        ics.IndexOf("UID:first@test", StringComparison.Ordinal)
            .Should().BeLessThan(ics.IndexOf("UID:second@test", StringComparison.Ordinal));
        ics.Split("BEGIN:VEVENT").Should().HaveCount(3);
    }

    [Fact]
    public void Build_IsDeterministicForIdenticalInput()
    {
        var first = IcsCalendarBuilder.Build(ProductId, [CreateEvent()], DtStamp);
        var second = IcsCalendarBuilder.Build(ProductId, [CreateEvent()], DtStamp);

        second.Should().Be(first);
    }

    [Fact]
    public void Build_WithNoEvents_StillProducesAValidEmptyCalendar()
    {
        var ics = IcsCalendarBuilder.Build(ProductId, [], DtStamp);

        ics.Should().Contain("BEGIN:VCALENDAR").And.Contain("END:VCALENDAR");
        ics.Should().NotContain("BEGIN:VEVENT");
    }
}
