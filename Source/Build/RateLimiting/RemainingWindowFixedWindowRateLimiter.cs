using System.Threading.RateLimiting;

namespace MMCA.Common.RateLimiting;

/// <summary>
/// The in-memory fixed-window limiter with an honest <see cref="MetadataName.RetryAfter"/> hint. The
/// framework's <see cref="FixedWindowRateLimiter"/> stamps every rejection with the whole window length,
/// so a 429 always said <c>Retry-After: 60</c> however much of the minute was left (Store local test
/// run 2, X-18). This wraps it, tracks when the current window opened, and rewrites the hint to the time
/// remaining until the next one, the figure the distributed <c>RedisFixedWindowRateLimiter</c> already
/// reports.
/// </summary>
/// <remarks>
/// <para>
/// One source file, compiled as an internal type into each package that runs an in-memory fixed
/// window: <c>MMCA.Common.API</c> (<c>AddCommonRateLimiting</c>), <c>MMCA.Common.Aspire</c> (the
/// gateway edge limiter) and <c>MMCA.Common.UI.Web</c> (the Blazor host's edge limiter), each through
/// a <c>Compile Include ... Link</c> in its project file. It cannot live in <c>MMCA.Common.Shared</c>,
/// the only project all three reference: <c>System.Threading.RateLimiting</c> ships in the ASP.NET
/// Core shared framework, which Shared (also loaded by Blazor WebAssembly) does not reference.
/// <c>MMCA.Common.Gateway</c> references no MMCA package and carries a renamed copy
/// (<c>GatewayRemainingWindowLimiter</c>), pinned to this one by a parity test.
/// </para>
/// <para>
/// The owning <see cref="PartitionedRateLimiter{TResource}"/> drives replenishment by calling
/// <see cref="TryReplenish"/> on its own timer (partition limiters never auto-replenish), and the inner
/// limiter opens a new window on the first call at least one window after the last. This class applies
/// the same rule to its own window start in the same call, so the two agree to within one timer tick.
/// </para>
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
        return new RejectedLease(TimeUntilNextWindow());
    }

    /// <summary>
    /// The rejection handed back in place of the framework's: not acquired, carrying only the
    /// remaining-window <see cref="MetadataName.RetryAfter"/> hint that the rate-limiter
    /// <c>OnRejected</c> callback turns into the 429's <c>Retry-After</c> header.
    /// </summary>
    private sealed class RejectedLease(TimeSpan retryAfter) : RateLimitLease
    {
        private static readonly string[] RetryAfterMetadataNames = [MetadataName.RetryAfter.Name];

        /// <inheritdoc />
        public override bool IsAcquired => false;

        /// <inheritdoc />
        public override IEnumerable<string> MetadataNames => RetryAfterMetadataNames;

        /// <inheritdoc />
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
