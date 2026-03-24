using FluentAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class DateRangeTests
{
    private static readonly DateOnly Jan1 = new(2025, 1, 1);
    private static readonly DateOnly Jan10 = new(2025, 1, 10);
    private static readonly DateOnly Jan20 = new(2025, 1, 20);
    private static readonly DateOnly Jan31 = new(2025, 1, 31);

    // ── Create ──
    [Fact]
    public void Create_WithValidRange_ReturnsSuccess()
    {
        var result = DateRange.Create(Jan1, Jan10);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Start.Should().Be(Jan1);
        result.Value.End.Should().Be(Jan10);
    }

    [Fact]
    public void Create_WithSameStartAndEnd_ReturnsSuccess()
    {
        var result = DateRange.Create(Jan1, Jan1);

        result.IsSuccess.Should().BeTrue();
        result.Value!.LengthInDays.Should().Be(0);
    }

    [Fact]
    public void Create_WithEndBeforeStart_ReturnsFailure()
    {
        var result = DateRange.Create(Jan10, Jan1);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "DateRange.Invalid");
    }

    // ── LengthInDays ──
    [Fact]
    public void LengthInDays_ReturnsCorrectDayCount()
    {
        var range = DateRange.Create(Jan1, Jan10).Value!;

        range.LengthInDays.Should().Be(9);
    }

    // ── Overlaps ──
    [Fact]
    public void Overlaps_OverlappingRanges_ReturnsTrue()
    {
        var a = DateRange.Create(Jan1, Jan20).Value!;
        var b = DateRange.Create(Jan10, Jan31).Value!;

        a.Overlaps(b).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_NonOverlappingRanges_ReturnsFalse()
    {
        var a = DateRange.Create(Jan1, Jan10).Value!;
        var b = DateRange.Create(Jan20, Jan31).Value!;

        a.Overlaps(b).Should().BeFalse();
    }

    [Fact]
    public void Overlaps_AdjacentRanges_ReturnsFalse()
    {
        var a = DateRange.Create(Jan1, Jan10).Value!;
        var b = DateRange.Create(Jan10, Jan20).Value!;

        a.Overlaps(b).Should().BeFalse();
    }

    // ── Contains ──
    [Fact]
    public void Contains_InstantWithinRange_ReturnsTrue()
    {
        var range = DateRange.Create(Jan1, Jan20).Value!;

        range.Contains(Jan10).Should().BeTrue();
    }

    [Fact]
    public void Contains_InstantOnStart_ReturnsTrue()
    {
        var range = DateRange.Create(Jan1, Jan20).Value!;

        range.Contains(Jan1).Should().BeTrue();
    }

    [Fact]
    public void Contains_InstantOnEnd_ReturnsTrue()
    {
        var range = DateRange.Create(Jan1, Jan20).Value!;

        range.Contains(Jan20).Should().BeTrue();
    }

    [Fact]
    public void Contains_InstantOutsideRange_ReturnsFalse()
    {
        var range = DateRange.Create(Jan1, Jan10).Value!;

        range.Contains(Jan20).Should().BeFalse();
    }

    // ── Deconstruct ──
    [Fact]
    public void Deconstruct_ReturnsTuple()
    {
        var range = DateRange.Create(Jan1, Jan10).Value!;

        var (start, end) = range;

        start.Should().Be(Jan1);
        end.Should().Be(Jan10);
    }
}
