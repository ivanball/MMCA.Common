namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// The PostgreSQL transport's own optional keys, bound from <c>MessageBus:PostgreSql</c>. Everything
/// else (the connection string, retry, redelivery, backpressure, topology provisioning) comes from the
/// shared <c>MessageBus</c> section.
/// </summary>
internal sealed class PostgreSqlTransportSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionPath = "MessageBus:PostgreSql";

    /// <summary>The default schema and role name.</summary>
    internal const string DefaultName = "transport";

    /// <summary>
    /// Gets the schema that holds the transport's tables. Defaults to <c>transport</c>, so the broker
    /// shares a database with the application's own tables without colliding. Letters, digits and
    /// underscores only, starting with a letter or underscore, at most 63 characters.
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Gets the role the startup migration creates (when missing), grants to the connecting user, and
    /// makes the owner of the schema and its tables, so every service that connects as a member of it
    /// can use the transport. Defaults to <c>transport</c>. Same character rules as <see cref="Schema"/>.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Gets a value indicating whether this host creates the transport schema at startup, before the
    /// receiver starts. On by default. The migration needs rights to create a role and a schema; a host
    /// whose database user has neither sets this to <see langword="false"/> and creates the schema out
    /// of band (the DDL is idempotent, so any host with the rights can run it once).
    /// </summary>
    public bool MigrateOnStartup { get; init; } = true;

    /// <summary>Gets the effective schema name.</summary>
    internal string EffectiveSchema => string.IsNullOrWhiteSpace(Schema) ? DefaultName : Schema;

    /// <summary>Gets the effective role name.</summary>
    internal string EffectiveRole => string.IsNullOrWhiteSpace(Role) ? DefaultName : Role;
}
