using System.Security.Claims;

namespace MMCA.Common.Infrastructure.Context;

/// <summary>
/// A per-scope carrier for a principal that did not arrive over HTTP, read by
/// <see cref="ImpersonatingCurrentUserService"/>.
/// <para>
/// The framework's <see cref="CurrentUserService"/> answers entirely from
/// <c>IHttpContextAccessor</c>, so a background execution has no principal at all: an
/// <c>IRequiresPermission</c> command would be denied and every write it made would be stamped with
/// the audit interceptor's system sentinel. The internal-command processor sets the principal it
/// captured at schedule time here, before it resolves the handler, so the deferred execution sees
/// exactly the identity the scheduling request had.
/// </para>
/// </summary>
/// <remarks>
/// Scoped, and written only by a background hop restoring a captured identity. Nothing in the
/// request pipeline ever writes to it, so an HTTP request resolves an empty carrier and reads
/// straight through to the HTTP principal. It is deliberately not an ambient (<c>AsyncLocal</c>)
/// value: an ambient override would leak across a scope boundary the moment a handler started work
/// on another thread.
/// </remarks>
internal sealed class ScopedUserOverride
{
    /// <summary>Gets the principal to act as for this scope, or null to read through to HTTP.</summary>
    public ClaimsPrincipal? Principal { get; private set; }

    /// <summary>
    /// Sets the principal this scope acts as. Called before any handler is resolved.
    /// </summary>
    /// <param name="principal">The principal to act as.</param>
    public void Set(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Principal = principal;
    }

    /// <summary>
    /// Drops any principal previously set, so the scope reads back through to its ambient identity.
    /// <para>
    /// Needed by the outbox processor, which restores one captured identity per row on a scope it
    /// keeps for the whole batch: without an explicit clear, a row raised by a user would answer for
    /// every later row raised by the system.
    /// </para>
    /// </summary>
    public void Clear() => Principal = null;
}
