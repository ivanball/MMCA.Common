using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// Fails a <see cref="PermissionRequirement"/> for a caller whose role the host lists under
/// <see cref="MultiFactorAuthorizationSettings.RequiredForRoles"/> when the session carries no second
/// factor. It never succeeds a requirement: <see cref="PermissionAuthorizationHandler"/> still grants,
/// and a policy with any failing handler fails, so this overrides that grant without changing it.
/// </summary>
/// <remarks>
/// It fails only a caller who holds the permission (the same <see cref="PermissionAuthorizationHandler.Holds"/>
/// evaluation), so a caller lacking the capability keeps the plain <c>403</c> and never learns about
/// the step-up, as in the CQRS authorization gate. Machine clients (<c>token_use=machine</c>) are
/// exempt, and service tokens carry no role. The failure reason's message is
/// <see cref="AuthErrorCodes.MultiFactorRequired"/>, which the result handler turns into the coded body.
/// </remarks>
/// <param name="permissionRegistry">The role-to-permission registry.</param>
/// <param name="settings">The bound role list.</param>
internal sealed class MultiFactorRoleAuthorizationHandler(
    IPermissionRegistry permissionRegistry,
    IOptions<MultiFactorAuthorizationSettings> settings) : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var roles = settings.Value.RequiredForRoles;
        if (roles.Count == 0)
        {
            return Task.CompletedTask;
        }

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || user.HasClaim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine)
            || !PermissionAuthorizationHandler.Holds(user, permissionRegistry, requirement.Permission)
            || !roles.Any(role => !string.IsNullOrWhiteSpace(role) && user.HasRole(role))
            || user.HasMultiFactor())
        {
            return Task.CompletedTask;
        }

        context.Fail(new AuthorizationFailureReason(this, AuthErrorCodes.MultiFactorRequired));
        return Task.CompletedTask;
    }
}
