using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Immutable value object representing a date-and-time range (inclusive on both ends).
/// Similar to <see cref="DateRange"/> but with <see cref="DateTime"/> precision.
/// Enforces that <see cref="End"/> is never before <see cref="Start"/> at creation time.
/// </summary>
public sealed record class DateTimeRange
{
    /// <summary>Gets the inclusive start date and time of the range.</summary>
    public DateTime Start { get; }

    /// <summary>Gets the inclusive end date and time of the range.</summary>
    public DateTime End { get; }

    private DateTimeRange(DateTime start, DateTime end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Creates a <see cref="DateTimeRange"/> after validating that <paramref name="end"/>
    /// is not before <paramref name="start"/>.
    /// </summary>
    /// <param name="start">The inclusive start date and time.</param>
    /// <param name="end">The inclusive end date and time.</param>
    /// <returns>A success result with the range, or a validation error if end precedes start.</returns>
    public static Result<DateTimeRange> Create(DateTime start, DateTime end)
        => end < start
            ? Result.Failure<DateTimeRange>(Error.Validation(
                "DateTimeRange.Invalid",
                "End date must be greater than or equal to start date."))
            : Result.Success(new DateTimeRange(start, end));

    /// <summary>Gets the elapsed time between <see cref="Start"/> and <see cref="End"/>.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Determines whether this range overlaps with <paramref name="other"/> using half-open interval logic.
    /// </summary>
    /// <param name="other">The range to test against.</param>
    /// <returns><see langword="true"/> if the ranges share at least one point in time.</returns>
    public bool Overlaps(DateTimeRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.End && End > other.Start;
    }

    /// <summary>Determines whether the specified instant falls within this range (inclusive).</summary>
    /// <param name="instant">The date and time to test.</param>
    /// <returns><see langword="true"/> if the instant is within the range.</returns>
    public bool Contains(DateTime instant) =>
        instant >= Start && instant <= End;

    /// <summary>Deconstructs the range into its start and end date-time values.</summary>
    /// <param name="start">The start date and time.</param>
    /// <param name="end">The end date and time.</param>
    public void Deconstruct(out DateTime start, out DateTime end)
    {
        start = Start;
        end = End;
    }
}
