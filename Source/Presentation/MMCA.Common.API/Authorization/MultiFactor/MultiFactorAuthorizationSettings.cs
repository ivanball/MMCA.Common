namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// The role-level second-factor requirement at the HTTP permission check, bound from
/// <c>Authorization:MultiFactor</c> by <c>AddAuthorizationPolicies</c>. Empty by default, which
/// changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// With a role listed, a caller in that role who holds the permission an endpoint's
/// <see cref="HasPermissionAttribute"/> names is refused (<c>403</c>,
/// <c>Authorization.MultiFactorRequired</c>) unless the session signed in with a second factor (the
/// <c>mfa</c> claim). Plain <c>[Authorize]</c> endpoints (sign-out, profile, the two-factor endpoints)
/// stay reachable, so the account can enroll. A caller lacking the permission keeps the plain
/// <c>403</c>, and machine clients and service tokens are not affected.
/// </para>
/// <para>
/// Each service binds its own section, so every service that hosts permission endpoints for a listed
/// role sets the list: <c>"Authorization": { "MultiFactor": { "RequiredForRoles": [ "Admin" ] } }</c>,
/// or <c>Authorization__MultiFactor__RequiredForRoles__0=Admin</c> in the environment. Blank entries
/// are ignored and roles match case-insensitively. Emptying the list switches the rule off.
/// </para>
/// </remarks>
public sealed class MultiFactorAuthorizationSettings
{
    /// <summary>The configuration section the settings bind from.</summary>
    public const string SectionName = "Authorization:MultiFactor";

    /// <summary>Gets the roles whose permission endpoints require a session with a second factor.</summary>
    public IList<string> RequiredForRoles { get; } = [];
}
