using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The built-in PostgreSQL transport (<c>MessageBus:Provider=PostgreSql</c>): topics, queues and
/// scheduled redeliveries kept in tables of one PostgreSQL database, so any PostgreSQL (Cloud SQL,
/// Amazon RDS, Azure Database for PostgreSQL, a container) is the broker, which is why it is core
/// rather than an adapter. Written against the same <see cref="IBrokerTransport"/> contract an adapter
/// package implements; its optional keys will bind from <c>MessageBus:PostgreSql</c>.
/// <para>
/// PLACEHOLDER on <c>feat/native-messaging</c>: the native Npgsql implementation (publish-time fan-out
/// into <c>{schema}.messages</c>, a <c>FOR UPDATE SKIP LOCKED</c> claim with a lease, LISTEN/NOTIFY
/// wake-up and <c>{schema}.dead_letters</c>) replaces this class before the branch merges.
/// </para>
/// </summary>
internal sealed class PostgreSqlBrokerTransport : IBrokerTransport
{
    /// <inheritdoc />
    public string Name => MessageBusProviders.PostgreSql;

    /// <inheritdoc />
    public TimeSpan? MaxRedeliveryDelay => null;

    /// <inheritdoc />
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        throw new NotSupportedException("PostgreSQL transport: native implementation pending on feat/native-messaging");
    }
}
