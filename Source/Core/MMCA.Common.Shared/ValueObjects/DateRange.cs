using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Immutable value object representing a date-only range (inclusive on both ends).
/// Enforces that <see cref="End"/> is never before <see cref="Start"/> at creation time.
/// </summary>
public sealed record class DateRange : ValueObject
{
    /// <summary>Gets the inclusive start date of the range.</summary>
    public DateOnly Start { get; }

    /// <summary>Gets the inclusive end date of the range.</summary>
    public DateOnly End { get; }

    private DateRange(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Creates a <see cref="DateRange"/> after validating that <paramref name="end"/>
    /// is not before <paramref name="start"/>.
    /// </summary>
    /// <param name="start">The inclusive start date.</param>
    /// <param name="end">The inclusive end date.</param>
    /// <returns>A success result with the date range, or a validation error if end precedes start.</returns>
    public static Result<DateRange> Create(DateOnly start, DateOnly end)
        => end < start
            ? Result.Failure<DateRange>(Error.Validation(
                "DateRange.Invalid",
                "End date must be greater than or equal to start date."))
            : Result.Success(new DateRange(start, end));

    /// <summary>Gets the number of days between <see cref="Start"/> and <see cref="End"/>.</summary>
    public int LengthInDays => End.DayNumber - Start.DayNumber;

    /// <summary>
    /// Determines whether this range overlaps with <paramref name="other"/> using half-open interval logic
    /// (Start is inclusive, End is exclusive for overlap comparison).
    /// </summary>
    /// <param name="other">The date range to test against.</param>
    /// <returns><see langword="true"/> if the ranges share at least one point in time.</returns>
    public bool Overlaps(DateRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.End && End > other.Start;
    }

    /// <summary>Determines whether the specified date falls within this range (inclusive).</summary>
    /// <param name="instant">The date to test.</param>
    /// <returns><see langword="true"/> if the date is within the range.</returns>
    public bool Contains(DateOnly instant) =>
        instant >= Start && instant <= End;

    /// <summary>Deconstructs the range into its start and end dates.</summary>
    /// <param name="start">The start date.</param>
    /// <param name="end">The end date.</param>
    public void Deconstruct(out DateOnly start, out DateOnly end)
    {
        start = Start;
        end = End;
    }
}
