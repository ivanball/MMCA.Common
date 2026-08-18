namespace MMCA.Common.API.RateLimiting;

/// <summary>
/// Selects the limiting algorithm used by the in-memory rate-limit partitions registered by
/// <c>AddCommonRateLimiting</c>. Both algorithms use the same one-minute window and the same
/// partition keys, so switching between them changes only how permits are replenished.
/// </summary>
public enum RateLimitAlgorithm
{
    /// <summary>
    /// A fixed one-minute window: the whole allowance becomes available again at the window
    /// boundary. Cheapest to run, but a caller can spend the allowance twice across a boundary
    /// (once at the end of one window, once at the start of the next).
    /// </summary>
    FixedWindow,

    /// <summary>
    /// A sliding one-minute window divided into <c>SegmentsPerWindow</c> segments: each segment's
    /// permits are returned as it ages out, so the boundary burst of
    /// <see cref="FixedWindow"/> is smoothed away at the cost of tracking one counter per segment.
    /// </summary>
    SlidingWindow
}
