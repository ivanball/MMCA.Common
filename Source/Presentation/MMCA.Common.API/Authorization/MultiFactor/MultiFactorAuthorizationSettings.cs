namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// The role-level second-factor requirement at the HTTP permission check, bound from
/// <c>Authorization:MultiFactor</c> by <c>AddAuthorizationPolicies</c>. Empty by default, which
/// changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// With a role listed, a listed role confers nothing on a bearer session that did not sign in with a
/// second factor (the <c>mfa</c> claim): at token validation the request's principal goes without the
/// role and without the permission claims that came with it, so every role and permission check sees an
/// account without the role, and a permission endpoint the role would have opened answers <c>403</c>
/// with <c>Authorization.MultiFactorRequired</c>. Plain <c>[Authorize]</c> endpoints (sign-out,
/// profile, the two-factor endpoints) stay reachable, so the account can enroll. A caller whose withheld
/// role would not have granted the permission keeps the plain <c>403</c>; roles that are not listed,
/// machine clients and service tokens are not affected, and the token itself is unchanged. When a
/// two-factor store is registered, the user-administration role action also refuses to give a listed
/// role to an account that has not enrolled.
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

    /// <summary>Gets the roles that confer nothing without a session with a second factor.</summary>
    public IList<string> RequiredForRoles { get; } = [];
}
