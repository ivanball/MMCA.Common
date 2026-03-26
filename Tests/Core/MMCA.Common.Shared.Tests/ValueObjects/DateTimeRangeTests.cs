using FluentAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class DateTimeRangeTests
{
    private static readonly DateTime T1 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T4 = new(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    // ── Create ──
    [Fact]
    public void Create_WithValidRange_ReturnsSuccess()
    {
        var result = DateTimeRange.Create(T1, T3);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Start.Should().Be(T1);
        result.Value.End.Should().Be(T3);
    }

    [Fact]
    public void Create_WithEndBeforeStart_ReturnsFailure()
    {
        var result = DateTimeRange.Create(T3, T1);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "DateTimeRange.Invalid");
    }

    // ── Duration ──
    [Fact]
    public void Duration_ReturnsCorrectTimeSpan()
    {
        var range = DateTimeRange.Create(T1, T3).Value!;

        range.Duration.Should().Be(TimeSpan.FromDays(1));
    }

    // ── Overlaps ──
    [Fact]
    public void Overlaps_OverlappingRanges_ReturnsTrue()
    {
        var a = DateTimeRange.Create(T1, T3).Value!;
        var b = DateTimeRange.Create(T2, T4).Value!;

        a.Overlaps(b).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_NonOverlappingRanges_ReturnsFalse()
    {
        var a = DateTimeRange.Create(T1, T2).Value!;
        var b = DateTimeRange.Create(T3, T4).Value!;

        a.Overlaps(b).Should().BeFalse();
    }

    // ── Contains ──
    [Fact]
    public void Contains_InstantWithinRange_ReturnsTrue()
    {
        var range = DateTimeRange.Create(T1, T3).Value!;

        range.Contains(T2).Should().BeTrue();
    }

    [Fact]
    public void Contains_InstantOutsideRange_ReturnsFalse()
    {
        var range = DateTimeRange.Create(T1, T2).Value!;

        range.Contains(T4).Should().BeFalse();
    }

    // ── Deconstruct ──
    [Fact]
    public void Deconstruct_ReturnsTuple()
    {
        var range = DateTimeRange.Create(T1, T3).Value!;

        var (start, end) = range;

        start.Should().Be(T1);
        end.Should().Be(T3);
    }
}
