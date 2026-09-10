namespace MMCA.Common.Infrastructure.Persistence.InternalCommands;

/// <summary>
/// The ambient context captured on the row at schedule time and restored around the deferred
/// execution: who asked for the work, which tenant they were acting in, and which request it belongs
/// to. Without it a scheduled command would run as an anonymous, tenant-less system call, which is
/// both an authorization hole (an <c>IRequiresPermission</c> command would be denied) and an audit
/// hole (its writes would be stamped with the system sentinel).
/// </summary>
/// <param name="UserId">The scheduling user's id, or null for system-scheduled work.</param>
/// <param name="UserRoles">The scheduling user's roles as a comma-separated list, or null.</param>
/// <param name="TenantId">The tenant the scheduling scope was resolved to, or null.</param>
/// <param name="CorrelationId">The scheduling request's correlation id, or null.</param>
/// <param name="TraceId">The W3C trace id current at schedule time, or null.</param>
/// <param name="SpanId">The W3C span id current at schedule time, or null.</param>
internal readonly record struct InternalCommandOrigin(
    UserIdentifierType? UserId,
    string? UserRoles,
    string? TenantId,
    string? CorrelationId,
    string? TraceId,
    string? SpanId);
