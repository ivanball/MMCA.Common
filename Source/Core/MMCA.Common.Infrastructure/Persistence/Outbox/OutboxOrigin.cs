namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// The ambient context captured on an outbox row when the event is written and restored around its
/// delivery: who raised it, which tenant they were acting in, and which interaction it belongs to.
/// <para>
/// Without it a delivered event runs as an anonymous, tenant-less system call one poll cycle after
/// the request that produced it, which is an authorization hole (a handler reading
/// <c>ICurrentUserService</c> sees nobody), an audit hole (its writes carry the system sentinel),
/// a tenancy hole (a shared-database host reads across tenants) and a correlation hole (the
/// delivery cannot be joined back to the request in the logs).
/// </para>
/// </summary>
/// <remarks>
/// The mirror of <c>InternalCommandOrigin</c>, deliberately: the two hops capture the same values
/// through the same helper so they cannot drift. It carries no trace context, because
/// <see cref="OutboxMessage.TraceId"/> and <see cref="OutboxMessage.SpanId"/> already do and are
/// stamped from the ambient <see cref="System.Diagnostics.Activity"/> rather than from a scope.
/// <para>
/// A <see langword="default"/> value means "nothing was captured", which is exactly what a caller with no
/// ambient context (a seeder, a design-time write, a directly-constructed test context) should
/// store, and exactly what every row written before these columns existed reads back as.
/// </para>
/// </remarks>
/// <param name="UserId">The raising user's id, or null for system-raised events.</param>
/// <param name="UserRoles">The raising user's roles as a comma-separated list, or null.</param>
/// <param name="TenantId">The tenant the raising scope was resolved to, or null.</param>
/// <param name="CorrelationId">The raising request's correlation id, or null.</param>
public readonly record struct OutboxOrigin(
    UserIdentifierType? UserId,
    string? UserRoles,
    string? TenantId,
    string? CorrelationId);
