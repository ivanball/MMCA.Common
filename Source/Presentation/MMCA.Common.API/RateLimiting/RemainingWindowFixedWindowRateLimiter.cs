using System.Threading.RateLimiting;

namespace MMCA.Common.API.RateLimiting;

/// <summary>
/// The in-memory fixed-window limiter with an honest <see cref="MetadataName.RetryAfter"/> hint. The
/// framework's <see cref="FixedWindowRateLimiter"/> stamps every rejection with the whole window length,
/// so a 429 always said <c>Retry-After: 60</c> however much of the minute was left (Store local test
/// run 2, X-18). This wraps it, tracks when the current window opened, and rewrites the hint to the time
/// remaining until the next one, the figure <see cref="RedisFixedWindowRateLimiter"/> already reports.
/// </summary>
/// <remarks>
/// The owning <see cref="PartitionedRateLimiter{TResource}"/> drives replenishment by calling
/// <see cref="TryReplenish"/> on its own timer (partition limiters never auto-replenish), and the inner
/// limiter opens a new window on the first call at least one window after the last. This class applies
/// the same rule to its own window start in the same call, so the two agree to within one timer tick.
/// </remarks>
internal sealed class RemainingWindowFixedWindowRateLimiter : ReplenishingRateLimiter
{
    private readonly FixedWindowRateLimiter _inner;
    private readonly TimeProvider _timeProvider;
    private long _windowStartTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemainingWindowFixedWindowRateLimiter"/> class.
    /// </summary>
    /// <param name="options">
    /// The fixed-window options. <see cref="FixedWindowRateLimiterOptions.AutoReplenishment"/> is
    /// forced off: the owning partitioned limiter replenishes, through <see cref="TryReplenish"/>.
    /// </param>
    /// <param name="timeProvider">Clock for the window start; tests substitute it.</param>
    public RemainingWindowFixedWindowRateLimiter(FixedWindowRateLimiterOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _inner = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            Window = options.Window,
            PermitLimit = options.PermitLimit,
            QueueLimit = options.QueueLimit,
            QueueProcessingOrder = options.QueueProcessingOrder,
            AutoReplenishment = false,
        });
        ReplenishmentPeriod = options.Window;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowStartTimestamp = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public override bool IsAutoReplenishing => false;

    /// <inheritdoc />
    public override TimeSpan ReplenishmentPeriod { get; }

    /// <inheritdoc />
    public override TimeSpan? IdleDuration => _inner.IdleDuration;

    /// <inheritdoc />
    public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();

    /// <inheritdoc />
    public override bool TryReplenish()
    {
        if (_timeProvider.GetElapsedTime(Volatile.Read(ref _windowStartTimestamp)) >= ReplenishmentPeriod)
        {
            Volatile.Write(ref _windowStartTimestamp, _timeProvider.GetTimestamp());
        }

        return _inner.TryReplenish();
    }

    /// <summary>The time left until the next window opens, never negative.</summary>
    /// <returns>The remaining part of the current window.</returns>
    internal TimeSpan TimeUntilNextWindow()
    {
        var remaining = ReplenishmentPeriod - _timeProvider.GetElapsedTime(Volatile.Read(ref _windowStartTimestamp));
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        WithRemainingRetryAfter(_inner.AttemptAcquire(permitCount));

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        WithRemainingRetryAfter(await _inner.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    // Only a rejection that carried a retry hint is rewritten: one with none (a request larger than
    // the whole allowance) has no later window that could satisfy it.
    private RateLimitLease WithRemainingRetryAfter(RateLimitLease lease)
    {
        if (lease.IsAcquired || !lease.TryGetMetadata(MetadataName.RetryAfter, out _))
        {
            return lease;
        }

        lease.Dispose();
        return RedisRateLimitLease.Rejected(TimeUntilNextWindow());
    }
}
