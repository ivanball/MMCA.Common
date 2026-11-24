using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// Creates the transport schema at startup (<c>MessageBus:PostgreSql:MigrateOnStartup</c>, on by
/// default). Registered ahead of the receiver, and hosted services start in registration order, so the
/// tables exist before the receiver claims its first row; a failure fails the host's start, as a
/// missing schema would fail every publish and receive.
/// </summary>
/// <param name="store">The message store that runs the migration.</param>
internal sealed class PostgreSqlSchemaMigrator(IPostgreSqlMessageStore store) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchemaAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
