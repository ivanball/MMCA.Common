namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>The resolved knobs of one <see cref="PostgreSqlReceiver"/>.</summary>
/// <param name="Concurrency">How many rows are processed at once.</param>
/// <param name="ClaimBatchSize">The most rows one claim takes (never more than the free slots).</param>
/// <param name="MigrateOnStartup">Whether to wait for the schema migration before receiving.</param>
/// <param name="ProvisionTopology">Whether to bind the queue's topics in <c>subscriptions</c> at start.</param>
/// <param name="Lease">How long a claim holds; renewed every third of it while the row is in flight.</param>
/// <param name="PollInterval">The longest the receiver sleeps without a notification before it looks again.</param>
internal sealed record PostgreSqlReceiverOptions(
    int Concurrency,
    int ClaimBatchSize,
    bool MigrateOnStartup,
    bool ProvisionTopology,
    TimeSpan Lease,
    TimeSpan PollInterval)
{
    /// <summary>The default lease: long enough to ride out a renewal missed to a database blip.</summary>
    internal static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(60);

    /// <summary>The default polling fallback when no notification arrives.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves the options from the shared settings: concurrency from
    /// <c>MessageBus:ConcurrentMessageLimit</c> (else the processor count), the claim batch from
    /// <c>MessageBus:PrefetchCount</c> (else the concurrency, and never above it).
    /// </summary>
    /// <param name="settings">The bound <c>MessageBus</c> section.</param>
    /// <param name="migrateOnStartup">Whether the host migrates the schema at startup.</param>
    /// <returns>The receiver options.</returns>
    internal static PostgreSqlReceiverOptions From(MessageBusSettings settings, bool migrateOnStartup)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var concurrency = Math.Max(1, BrokerTransportPolicies.ResolveConcurrentMessageLimit(settings) ?? Environment.ProcessorCount);
        var batch = Math.Clamp(BrokerTransportPolicies.ResolvePrefetchCount(settings) ?? concurrency, 1, concurrency);

        return new PostgreSqlReceiverOptions(
            concurrency,
            batch,
            migrateOnStartup,
            settings.ProvisionTopology,
            DefaultLease,
            DefaultPollInterval);
    }
}
