using AwesomeAssertions;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Coverage for <see cref="ScheduledJobRunner.TryGetNextOccurrence"/>: the cron grammar the
/// scheduler exposes, its strictly-after semantics, and the UTC-only clock that keeps a schedule
/// stable across daylight saving transitions.
/// </summary>
public sealed class CronosNextOccurrenceTests
{
    [Theory]
    // Five-minute step from a boundary instant: strictly after, so 00:00 yields 00:05, never 00:00.
    [InlineData("*/5 * * * *", "2000-01-01T00:00:00Z", "2000-01-01T00:05:00Z")]
    [InlineData("*/5 * * * *", "2000-01-01T00:01:00Z", "2000-01-01T00:05:00Z")]
    // Hourly on the hour.
    [InlineData("0 * * * *", "2000-01-01T00:00:00Z", "2000-01-01T01:00:00Z")]
    // Daily at 03:00, asked just after it passed: rolls to tomorrow.
    [InlineData("0 3 * * *", "2000-01-01T03:00:01Z", "2000-01-02T03:00:00Z")]
    // Mondays at 02:00 (2000-01-01 was a Saturday).
    [InlineData("0 2 * * 1", "2000-01-01T00:00:00Z", "2000-01-03T02:00:00Z")]
    // Lists and ranges.
    [InlineData("0,30 * * * *", "2000-01-01T00:05:00Z", "2000-01-01T00:30:00Z")]
    [InlineData("0 9-17 * * *", "2000-01-01T00:00:00Z", "2000-01-01T09:00:00Z")]
    public void TryGetNextOccurrence_ValidExpression_ReturnsTheFirstOccurrenceStrictlyAfter(
        string cron,
        string afterUtc,
        string expectedUtc)
    {
        var next = ScheduledJobRunner.TryGetNextOccurrence(cron, ParseUtc(afterUtc), out var error);

        error.Should().BeNull();
        next.Should().Be(ParseUtc(expectedUtc));
    }

    // ── UTC semantics: the schedule never doubles, skips, or shifts across a DST transition ──
    [Fact]
    public void TryGetNextOccurrence_AcrossAUsDstSpringForward_AdvancesByExactlyOneDay()
    {
        // 2000-04-02 02:00 local was the US spring-forward instant. In UTC there is no such hour, so
        // a daily 03:00 UTC schedule simply advances 24 hours; a local-time scheduler would have had
        // to decide whether the occurrence existed at all.
        var next = ScheduledJobRunner.TryGetNextOccurrence("0 3 * * *", ParseUtc("2000-04-01T03:00:01Z"), out var error);

        error.Should().BeNull();
        next.Should().Be(ParseUtc("2000-04-02T03:00:00Z"));
    }

    [Fact]
    public void TryGetNextOccurrence_AcrossAUsDstFallBack_RunsExactlyOnce()
    {
        // 2000-10-29 was the US fall-back date, where a local 01:30 happens twice. In UTC the hourly
        // schedule steps once per hour, so no occurrence is ever executed twice.
        var next = ScheduledJobRunner.TryGetNextOccurrence("30 * * * *", ParseUtc("2000-10-29T05:30:00Z"), out var error);

        error.Should().BeNull();
        next.Should().Be(ParseUtc("2000-10-29T06:30:00Z"));
    }

    [Fact]
    public void TryGetNextOccurrence_ReturnsUtcKind()
    {
        var next = ScheduledJobRunner.TryGetNextOccurrence("0 * * * *", ParseUtc("2000-01-01T00:00:00Z"), out _);

        next.Should().NotBeNull();
        next!.Value.Kind.Should().Be(DateTimeKind.Utc, "every scheduling timestamp is UTC end to end");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a cron expression")]
    // Four fields: too few.
    [InlineData("* * * *")]
    // Minute out of range.
    [InlineData("99 * * * *")]
    public void TryGetNextOccurrence_InvalidOrUnsatisfiableExpression_ReturnsNullWithoutThrowing(string cron)
    {
        var next = ScheduledJobRunner.TryGetNextOccurrence(cron, ParseUtc("2000-01-01T00:00:00Z"), out _);

        next.Should().BeNull("an expression the scheduler cannot honor must park the row, never crash the runner");
    }

    [Fact]
    public void TryGetNextOccurrence_InvalidExpression_ReportsTheParseError()
    {
        ScheduledJobRunner.TryGetNextOccurrence("not a cron expression", ParseUtc("2000-01-01T00:00:00Z"), out var error);

        error.Should().NotBeNullOrWhiteSpace("the message is what lands in LastError for an operator to read");
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
}
