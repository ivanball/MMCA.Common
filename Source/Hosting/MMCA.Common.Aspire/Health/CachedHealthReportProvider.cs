using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// Runs a health-check set at most once per cache window, with single flight: concurrent callers
/// that arrive on a cold or expired entry wait for the one probe round in progress instead of each
/// starting their own.
/// </summary>
/// <remarks>
/// <para>
/// SEC-Common-71, SEC-ADC-17. Registered as a singleton, one entry per endpoint (the full report and
/// the readiness subset run different predicates and must not share a result).
/// </para>
/// <para>
/// A cached report is served even while it is being refreshed, so a slow dependency cannot turn a
/// probe flood into a queue of waiting requests. The very first call after startup has nothing to
/// serve and does wait, which is correct: an answer before the first probe would be a guess.
/// </para>
/// </remarks>
/// <param name="healthCheckService">The health-check service that executes the probes.</param>
/// <param name="options">The cache window.</param>
/// <param name="timeProvider">Clock, injected so the window is testable without waiting.</param>
public sealed class CachedHealthReportProvider(
    HealthCheckService healthCheckService,
    IOptions<HealthReportCacheOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _gate = new();

    private sealed class Entry : IDisposable
    {
        public HealthReport? Report { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public SemaphoreSlim Refresh { get; } = new(1, 1);

        public void Dispose() => Refresh.Dispose();
    }

    /// <summary>
    /// The report for <paramref name="endpointKey"/>: the cached one while it is fresh, otherwise a
    /// newly computed one. Only one caller per key computes at a time.
    /// </summary>
    /// <param name="endpointKey">Identifies the check set, so two endpoints never share a result.</param>
    /// <param name="predicate">Which registered checks take part, exactly as <c>HealthCheckOptions</c> would select them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health report to render.</returns>
    public async Task<HealthReport> GetReportAsync(
        string endpointKey,
        Func<HealthCheckRegistration, bool>? predicate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);

        var window = TimeSpan.FromSeconds(options.Value.CacheSeconds);
        if (window <= TimeSpan.Zero)
        {
            return await healthCheckService.CheckHealthAsync(predicate, cancellationToken).ConfigureAwait(false);
        }

        var entry = GetEntry(endpointKey);
        var now = timeProvider.GetUtcNow();

        if (TryReadFresh(entry, now) is { } fresh)
        {
            return fresh;
        }

        // Single flight. A caller that cannot take the refresh slot serves the stale report rather
        // than queueing, so a slow dependency never converts a probe flood into a request backlog.
        if (!await entry.Refresh.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            var stale = ReadAny(entry);
            if (stale is not null)
            {
                return stale;
            }

            // Nothing cached yet: there is no answer to serve, so wait for the round in progress.
            await entry.Refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Re-check: the holder we waited behind may have just published a fresh report.
            if (TryReadFresh(entry, timeProvider.GetUtcNow()) is { } published)
            {
                return published;
            }

            var report = await healthCheckService.CheckHealthAsync(predicate, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                entry.Report = report;
                entry.ExpiresAt = timeProvider.GetUtcNow().Add(window);
            }

            return report;
        }
        finally
        {
            entry.Refresh.Release();
        }
    }

    private Entry GetEntry(string endpointKey)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(endpointKey, out var entry))
            {
                entry = new Entry();
                _entries[endpointKey] = entry;
            }

            return entry;
        }
    }

    private HealthReport? TryReadFresh(Entry entry, DateTimeOffset now)
    {
        lock (_gate)
        {
            return entry.Report is not null && now < entry.ExpiresAt ? entry.Report : null;
        }
    }

    private HealthReport? ReadAny(Entry entry)
    {
        lock (_gate)
        {
            return entry.Report;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Dispose();
            }

            _entries.Clear();
        }
    }
}
