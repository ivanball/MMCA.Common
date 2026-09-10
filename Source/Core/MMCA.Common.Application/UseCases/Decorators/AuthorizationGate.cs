using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// The authorization decision the command and query authorization decorators share: the
/// <see cref="IRequiresPermission"/> capability check followed by the <see cref="IRequiresMfa"/>
/// step-up check.
/// </summary>
/// <remarks>
/// Factored out so the two decorators cannot drift: they differ only in which generic parameter names
/// the request, and a rule enforced on commands but not on queries is exactly the kind of gap that
/// reads as working until someone moves a use case from one to the other.
/// </remarks>
internal static class AuthorizationGate
{
    /// <summary>
    /// Evaluates both gates against one request.
    /// </summary>
    /// <param name="request">The command or query.</param>
    /// <param name="currentUser">The caller.</param>
    /// <param name="permissionRegistry">The host's permission registry.</param>
    /// <param name="requestTypeName">The request type name, used in the error payload and the metric.</param>
    /// <returns>
    /// <see langword="null"/> when the request may run, or the <see cref="ErrorType.Forbidden"/>
    /// failure to short-circuit with. The denial metric is recorded here, so a caller only has to
    /// shape the failure.
    /// </returns>
    internal static Error? Evaluate(
        object? request,
        ICurrentUserService currentUser,
        IPermissionRegistry permissionRegistry,
        string requestTypeName)
    {
        if (request is IRequiresPermission requiresPermission
            && !permissionRegistry.HasPermission(currentUser.Roles, requiresPermission.Permission))
        {
            CqrsMetrics.RecordAuthorizationDenied(requestTypeName);

            return Error.Forbidden(
                "Authorization.PermissionDenied",
                $"The current user does not hold the '{requiresPermission.Permission}' permission.",
                source: requestTypeName);
        }

        // Second, so a caller who does not hold the capability at all is answered by the capability
        // gate and never learns which use cases additionally demand a step-up.
        if (request is IRequiresMfa && !currentUser.User.HasMultiFactor())
        {
            CqrsMetrics.RecordAuthorizationDenied(requestTypeName);

            return Error.Forbidden(
                "Authorization.MultiFactorRequired",
                "This operation requires a second authentication factor. Sign in again with your authenticator code.",
                source: requestTypeName);
        }

        return null;
    }
}
