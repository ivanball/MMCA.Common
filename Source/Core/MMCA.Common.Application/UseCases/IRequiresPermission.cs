namespace MMCA.Common.Application.UseCases;

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
public interface IRequiresPermission
{
    /// <summary>
    /// The permission the caller must hold (e.g. "catalog.products.write").
    /// Must match a value the host's <c>IPermissionRegistry</c> knows about; an unknown permission
    /// is granted by no role and therefore denies every caller.
    /// </summary>
    string Permission { get; }
}
