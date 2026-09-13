namespace MMCA.Common.Application.UseCases.Markers;

/// <summary>
/// Marker interface for commands and queries that require a fine-grained permission.
/// The <see cref="Decorators.AuthorizationCommandDecorator{TCommand,TResult}"/> and
/// <see cref="Decorators.AuthorizationQueryDecorator{TQuery,TResult}"/> resolve the caller's roles
/// from <c>ICurrentUserService</c>, ask <c>IPermissionRegistry</c> whether any of them grants
/// <see cref="Permission"/>, and short-circuit with a
/// <see cref="Common.Shared.Abstractions.ErrorType.Forbidden"/> failure when none does.
/// <para>
/// Opting in is per request type: a command or query that does not implement this interface passes
/// through the decorator untouched, so endpoint-level <c>[Authorize]</c> policies remain the only
/// gate for everything that has not opted in.
/// </para>
/// </summary>
/// <remarks>
/// The gate evaluates the CURRENT principal, and for a command that does not run inline that is the
/// principal restored from the scheduling request: an internal command carries the scheduling user
/// and tenant on its row and <c>InternalCommandProcessor</c> puts them back before the handler runs,
/// and an integration-event consumer restores the same context from the message headers. So never
/// mark a command that can be scheduled without an operator principal behind it (work queued by a
/// buyer, by an anonymous webhook, or by a system sweep with no user at all): the restored principal
/// holds no roles, the registry grants nothing, and the gate denies the command instead of running
/// it. Gate such work at the edge that accepts it, and leave the deferred command ungated.
/// </remarks>
public interface IRequiresPermission
{
    /// <summary>
    /// The permission the caller must hold (e.g. "catalog.products.write").
    /// Must match a value the host's <c>IPermissionRegistry</c> knows about; an unknown permission
    /// is granted by no role and therefore denies every caller.
    /// </summary>
    string Permission { get; }
}
