using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// Fails a <see cref="PermissionRequirement"/> with the coded second-factor reason when the permission
/// was withheld from the caller together with a role the host lists under
/// <see cref="MultiFactorAuthorizationSettings.RequiredForRoles"/>. It never succeeds a requirement.
/// </summary>
/// <remarks>
/// <para>
/// The bearer token validation (<see cref="MultiFactorBearerWithholding"/>) already took the listed
/// role and its permission claims off a principal whose session carries no second factor, so the
/// permission check alone refuses that caller. This handler only turns the refusal into the coded one:
/// it fails when the principal carries a <see cref="AuthClaimTypes.MultiFactorWithheldRole"/> marker,
/// does not hold the permission through what it kept, and the permission is among the withheld
/// permission markers or the registry grants it to a withheld role. The failure reason's message is
/// <see cref="AuthErrorCodes.MultiFactorRequired"/>, which the result handler turns into the coded body.
/// </para>
/// <para>
/// A caller whose withheld role would not have granted the permission keeps the plain <c>403</c> and
/// never learns about the step-up, as in the CQRS authorization gate. A principal with no marker (a
/// session with the <c>mfa</c> claim, a role that is not listed, a machine client, a service token)
/// gets exactly the answer the permission check gives.
/// </para>
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

        if (settings.Value.RequiredForRoles.Count == 0)
        {
            return Task.CompletedTask;
        }

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || !user.HasMultiFactorWithheldRole()
            || PermissionAuthorizationHandler.Holds(user, permissionRegistry, requirement.Permission))
        {
            return Task.CompletedTask;
        }

        if (user.HasClaim(AuthClaimTypes.MultiFactorWithheldPermission, requirement.Permission)
            || permissionRegistry.HasPermission(user.GetMultiFactorWithheldRoles(), requirement.Permission))
        {
            context.Fail(new AuthorizationFailureReason(this, AuthErrorCodes.MultiFactorRequired));
        }

        return Task.CompletedTask;
    }
}
