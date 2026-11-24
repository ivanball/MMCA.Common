namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The receive-side policies every broker transport shares, as pure functions of
/// <see cref="MessageBusSettings"/>, so the receive pipeline and every transport read one definition:
/// the exponential in-process retry delay, the second-level redelivery intervals (clamped to the
/// transport's delay cap), and the two backpressure knobs.
/// </summary>
/// <remarks>
/// In-process retry absorbs a blip measured in seconds; second-level redelivery re-enqueues the
/// message on the host's queue over <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> (one
/// minute, ten minutes, one hour by default), so an outage measured in minutes or hours does not
/// dead-letter the event. The receive pipeline exhausts every in-process retry before it asks for a
/// redelivery.
/// </remarks>
public static class BrokerTransportPolicies
{
    /// <summary>
    /// The delay before in-process retry number <paramref name="retryNumber"/> (1 for the first
    /// retry): <see cref="MessageBusSettings.RetryMinIntervalSeconds"/> doubled per retry, capped at
    /// <see cref="MessageBusSettings.RetryMaxIntervalSeconds"/>. With the defaults (1 and 30 seconds)
    /// the five retries wait 1, 2, 4, 8 and 16 seconds.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <param name="retryNumber">The 1-based retry number.</param>
    /// <returns>The delay; <see cref="TimeSpan.Zero"/> when the minimum interval is zero.</returns>
    public static TimeSpan RetryDelay(MessageBusSettings settings, int retryNumber)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryNumber, 1);

        var minSeconds = Math.Max(0, settings.RetryMinIntervalSeconds);
        var maxSeconds = Math.Max(minSeconds, settings.RetryMaxIntervalSeconds);

        // Doubling past 2^30 overflows nothing useful; the cap is reached long before.
        var exponent = Math.Min(retryNumber - 1, 30);
        var seconds = Math.Min(maxSeconds, minSeconds * Math.Pow(2, exponent));

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Maps <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> to the second-level redelivery
    /// intervals, falling back to <see cref="MessageBusSettings.DefaultRedeliveryIntervalsSeconds"/>
    /// when nothing is configured. Non-positive entries are dropped: a zero or negative interval would
    /// redeliver immediately, which is what the in-process retry already does, and would turn the
    /// second level into a hot loop. Returns an empty array when nothing survives (a configured
    /// <c>[0]</c>), which turns second-level redelivery off.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <returns>The positive redelivery intervals, in configured order.</returns>
    public static TimeSpan[] BuildRedeliveryIntervals(MessageBusSettings settings) =>
        BuildRedeliveryIntervals(settings, maxRedeliveryDelay: null);

    /// <summary>
    /// Maps <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> exactly as
    /// <see cref="BuildRedeliveryIntervals(MessageBusSettings)"/> does, then shortens every interval
    /// longer than <paramref name="maxRedeliveryDelay"/> to it, for a transport whose broker caps a
    /// message delay (<see cref="IBrokerTransport.MaxRedeliveryDelay"/>; Amazon SQS allows 15
    /// minutes). A non-positive cap is ignored.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <param name="maxRedeliveryDelay">The transport's delay cap, or <see langword="null"/> for none.</param>
    /// <returns>The positive, clamped redelivery intervals, in configured order.</returns>
    public static TimeSpan[] BuildRedeliveryIntervals(MessageBusSettings settings, TimeSpan? maxRedeliveryDelay)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var cap = maxRedeliveryDelay is { } value && value > TimeSpan.Zero ? value : TimeSpan.MaxValue;

        return [.. (settings.RedeliveryIntervalsSeconds ?? MessageBusSettings.DefaultRedeliveryIntervalsSeconds)
            .Where(seconds => seconds > 0)
            .Select(seconds => TimeSpan.FromSeconds(seconds))
            .Select(interval => interval > cap ? cap : interval)];
    }

    /// <summary>
    /// The prefetch window a transport applies, or <see langword="null"/> to keep its own default.
    /// Range-guarded HERE rather than with a <c>[Range]</c> attribute: <c>AddBrokerMessaging</c> binds
    /// the section with <c>Get&lt;MessageBusSettings&gt;()</c>, which never runs DataAnnotations, so a
    /// value of zero or less is left unapplied instead of stalling the receiver.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <returns>The positive prefetch count, or <see langword="null"/>.</returns>
    public static int? ResolvePrefetchCount(MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.PrefetchCount is > 0 ? settings.PrefetchCount : null;
    }

    /// <summary>
    /// The concurrency cap a transport's receiver applies, or <see langword="null"/> to keep its own
    /// default. Guarded the same way as <see cref="ResolvePrefetchCount"/>.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <returns>The positive concurrent message limit, or <see langword="null"/>.</returns>
    public static int? ResolveConcurrentMessageLimit(MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.ConcurrentMessageLimit is > 0 ? settings.ConcurrentMessageLimit : null;
    }
}
