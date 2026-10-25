namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The PostgreSQL transport's own optional keys, bound from <c>MessageBus:PostgreSql</c>. Everything
/// else (the connection string, retry, redelivery, backpressure, the endpoint prefix) comes from the
/// shared <c>MessageBus</c> section.
/// </summary>
internal sealed class PostgreSqlTransportSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionPath = "MessageBus:PostgreSql";

    /// <summary>MassTransit's default schema and role name.</summary>
    internal const string DefaultName = "transport";

    /// <summary>
    /// The schema that holds the transport's tables and functions. Defaults to <c>transport</c>, so the
    /// broker shares a database with the application's own <c>dbo</c> tables without colliding.
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// The role the migration creates, owns the schema with and grants to the connecting user.
    /// Defaults to <c>transport</c>.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Whether this host creates (or updates) the transport schema at startup, through MassTransit's
    /// migration hosted service, before the bus starts. On by default. The migration opens its own
    /// connections from the connection string with password authentication (it cannot take the shared
    /// data source), and it needs rights to create a role and a schema; a host whose broker database
    /// authenticates with IAM tokens sets this to <see langword="false"/> and runs the migration out of
    /// band, from a deployment step that connects with a password.
    /// </summary>
    public bool MigrateOnStartup { get; init; } = true;

    /// <summary>Gets the effective schema name.</summary>
    internal string EffectiveSchema => string.IsNullOrWhiteSpace(Schema) ? DefaultName : Schema;

    /// <summary>Gets the effective role name.</summary>
    internal string EffectiveRole => string.IsNullOrWhiteSpace(Role) ? DefaultName : Role;
}
