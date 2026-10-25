using MassTransit;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The receive-side policies every broker transport applies, in one place so a built-in transport and
/// an adapter transport cannot drift: second-level delayed redelivery, the exponential in-process
/// retry, and the two backpressure knobs, all driven by <see cref="MessageBusSettings"/>.
/// </summary>
/// <remarks>
/// Second-level redelivery (<c>UseDelayedRedelivery</c>) sits ABOVE the in-process retry policy:
/// in-process retry absorbs a blip measured in seconds, delayed redelivery reschedules the message
/// through the broker over <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> (one minute,
/// ten minutes, one hour by default) so an outage measured in minutes or hours does not dead-letter
/// the event. It is registered before <c>UseMessageRetry</c> so the retry filter runs innermost:
/// every immediate attempt is exhausted before a redelivery is scheduled.
/// <para>
/// Whether a transport applies redelivery is the transport's call, because it depends on the broker:
/// the PostgreSQL SQL transport, Azure Service Bus and Amazon SQS schedule messages natively and apply it unconditionally (SQS with
/// a default of its own inside its 15-minute delay cap), while RabbitMQ needs
/// the <c>rabbitmq_delayed_message_exchange</c> plugin and gates it behind
/// <see cref="MessageBusSettings.EnableDelayedRedelivery"/>.
/// </para>
/// </remarks>
public static class BrokerTransportPolicies
{
    /// <summary>
    /// Applies the shared receive policies to a transport's bus factory configurator, in the order
    /// they must run: delayed redelivery (when <paramref name="applyDelayedRedelivery"/> and at least
    /// one interval survives <see cref="BuildRedeliveryIntervals(MessageBusSettings)"/>), then the
    /// exponential in-process retry, then backpressure. The caller still ends with
    /// <c>ConfigureEndpoints(context)</c>.
    /// </summary>
    /// <param name="cfg">The transport's bus factory configurator.</param>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <param name="applyDelayedRedelivery">Whether this transport schedules second-level redelivery.</param>
    public static void ApplyReceivePolicies(IBusFactoryConfigurator cfg, MessageBusSettings settings, bool applyDelayedRedelivery)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(settings);

        ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery ? BuildRedeliveryIntervals(settings) : []);
    }

    /// <summary>
    /// Applies the shared receive policies with redelivery intervals the transport resolved itself,
    /// for a transport whose broker needs a default of its own or validates the intervals against a
    /// delay cap (build them with
    /// <see cref="BuildRedeliveryIntervals(MessageBusSettings, IReadOnlyList{int})"/>). Same order as
    /// the other overload: delayed redelivery (skipped when <paramref name="redeliveryIntervals"/> is
    /// empty), then the exponential in-process retry, then backpressure.
    /// </summary>
    /// <param name="cfg">The transport's bus factory configurator.</param>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <param name="redeliveryIntervals">The second-level redelivery intervals; empty turns redelivery off.</param>
    public static void ApplyReceivePolicies(IBusFactoryConfigurator cfg, MessageBusSettings settings, IReadOnlyList<TimeSpan> redeliveryIntervals)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(redeliveryIntervals);

        if (redeliveryIntervals.Count > 0)
        {
            TimeSpan[] intervals = [.. redeliveryIntervals];
            cfg.UseDelayedRedelivery(r => r.Intervals(intervals));
        }

        ApplyMessageRetry(cfg, settings);
        ApplyBackpressure(cfg, settings);
    }

    /// <summary>
    /// Applies the exponential in-process retry (<c>UseMessageRetry</c>) configured by
    /// <see cref="MessageBusSettings.RetryLimit"/>, <see cref="MessageBusSettings.RetryMinIntervalSeconds"/>
    /// and <see cref="MessageBusSettings.RetryMaxIntervalSeconds"/>, so a transient handler failure is
    /// retried in-process instead of dead-lettering on the first exception.
    /// </summary>
    /// <param name="cfg">The transport's bus factory configurator.</param>
    /// <param name="settings">The bound message-bus settings.</param>
    public static void ApplyMessageRetry(IBusFactoryConfigurator cfg, MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(settings);

        cfg.UseMessageRetry(r => r.Exponential(
            settings.RetryLimit,
            TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds),
            TimeSpan.FromSeconds(settings.RetryMaxIntervalSeconds),
            TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds)));
    }

    /// <summary>
    /// Applies the two backpressure knobs to the bus factory configurator, which is where MassTransit
    /// carries them onto every receive endpoint <c>ConfigureEndpoints</c> then builds. Both are
    /// optional and both are range-guarded HERE rather than with a <c>[Range]</c> attribute:
    /// <c>AddBrokerMessaging</c> binds this section with <c>Get&lt;MessageBusSettings&gt;()</c>, which
    /// never runs DataAnnotations, so an annotation would document the bound without enforcing it. A
    /// value of zero or less is left unapplied, so the transport keeps its own default instead of
    /// receiving a window that would stall the endpoint.
    /// </summary>
    /// <param name="cfg">The transport's bus factory configurator.</param>
    /// <param name="settings">The bound message-bus settings.</param>
    public static void ApplyBackpressure(IBusFactoryConfigurator cfg, MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.PrefetchCount is > 0)
        {
            cfg.PrefetchCount = settings.PrefetchCount.Value;
        }

        if (settings.ConcurrentMessageLimit is > 0)
        {
            cfg.ConcurrentMessageLimit = settings.ConcurrentMessageLimit.Value;
        }
    }

    /// <summary>
    /// Maps <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> to the
    /// <see cref="TimeSpan"/> array MassTransit's redelivery configurator expects, falling back to
    /// <see cref="MessageBusSettings.DefaultRedeliveryIntervalsSeconds"/> when nothing is configured.
    /// Non-positive entries are dropped: a zero or negative interval schedules an immediate
    /// redelivery, which is what <c>UseMessageRetry</c> already does and would turn the second level
    /// into a hot loop. Returns an empty array when nothing survives (a configured <c>[0]</c>), and the
    /// caller then skips the filter entirely rather than registering a redelivery policy with no
    /// attempts.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <returns>The positive redelivery intervals, in configured order.</returns>
    public static TimeSpan[] BuildRedeliveryIntervals(MessageBusSettings settings) =>
        BuildRedeliveryIntervals(settings, MessageBusSettings.DefaultRedeliveryIntervalsSeconds);

    /// <summary>
    /// Maps <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> exactly as
    /// <see cref="BuildRedeliveryIntervals(MessageBusSettings)"/> does, but falls back to a
    /// transport-specific default when nothing is configured. A transport whose broker caps a message
    /// delay passes a default inside the cap, so an unconfigured host runs, and validates the result,
    /// so an explicitly configured interval over the cap still fails at registration.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <param name="defaultIntervalsSeconds">The intervals, in seconds, used when none are configured.</param>
    /// <returns>The positive redelivery intervals, in configured order.</returns>
    public static TimeSpan[] BuildRedeliveryIntervals(MessageBusSettings settings, IReadOnlyList<int> defaultIntervalsSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(defaultIntervalsSeconds);

        return [.. (settings.RedeliveryIntervalsSeconds ?? defaultIntervalsSeconds)
            .Where(seconds => seconds > 0)
            .Select(seconds => TimeSpan.FromSeconds(seconds))];
    }
}
