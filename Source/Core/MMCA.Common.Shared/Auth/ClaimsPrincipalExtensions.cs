using System.Globalization;
using System.Security.Claims;

namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Reads the framework's identity claims off a <see cref="ClaimsPrincipal"/>.
/// <para>
/// Tokens carry the user identifier in the standard <c>sub</c> claim only. That single value reaches
/// readers under two different claim types depending on the pipeline that produced the principal: the
/// JWT bearer handler maps inbound <c>sub</c> onto
/// <see cref="ClaimTypes.NameIdentifier"/>, while a handler that materializes an identity straight from
/// a token's claims (the session-cookie handler) leaves the raw <c>sub</c> in place. Every framework
/// reader goes through <see cref="FindUserIdValue"/> so both shapes resolve identically, and a consumer
/// that changes its claim mapping does not silently lose the current user.
/// </para>
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the raw user-identifier claim value (<c>sub</c>, falling back to the mapped
    /// <see cref="ClaimTypes.NameIdentifier"/>), or <see langword="null"/> when the principal carries
    /// neither.
    /// </summary>
    /// <param name="principal">The principal to read; a null principal yields null.</param>
    public static string? FindUserIdValue(this ClaimsPrincipal? principal) =>
        principal?.FindFirst(AuthClaimTypes.Subject)?.Value
        ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>
    /// Returns the user identifier parsed into <c>UserIdentifierType</c>, or <see langword="null"/>
    /// when the claim is absent or unparsable.
    /// </summary>
    /// <remarks>
    /// Parsing goes through <see cref="IParsable{TSelf}"/> in the invariant culture, matching the
    /// writer (claims are formatted with <see cref="CultureInfo.InvariantCulture"/>) and staying
    /// correct if the solution-wide identifier alias changes shape.
    /// </remarks>
    /// <param name="principal">The principal to read; a null principal yields null.</param>
    public static UserIdentifierType? GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal.FindUserIdValue();
        return UserIdentifierType.TryParse(value, CultureInfo.InvariantCulture, out var userId) ? userId : null;
    }

    /// <summary>
    /// Returns every role value the principal carries, regardless of how the authentication
    /// pipeline mapped the role claim: the standard <see cref="ClaimTypes.Role"/> URI, or the raw
    /// <c>role</c>/<c>roles</c> claim an identity provider emits when inbound-claim mapping is off.
    /// </summary>
    /// <remarks>
    /// SECURITY: this is the framework's ONE definition of "the caller's roles". A reader that uses
    /// the narrower BCL <see cref="ClaimsPrincipal.IsInRole(string)"/> sees only the identity's own
    /// role claim type, so it can disagree with the authorization handler about who is privileged,
    /// and a check built on that disagreement (a cache bypass, an elevated payload) silently fails
    /// open. Every role read goes through here.
    /// </remarks>
    /// <param name="principal">The principal to read; a null principal yields an empty sequence.</param>
    public static IEnumerable<string> GetRoleValues(this ClaimsPrincipal? principal) =>
        principal is null
            ? []
            : principal.Claims
                .Where(claim =>
                    string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal)
                    || string.Equals(claim.Type, "role", StringComparison.Ordinal)
                    || string.Equals(claim.Type, "roles", StringComparison.Ordinal))
                .Select(claim => claim.Value);

    /// <summary>
    /// Whether the principal holds <paramref name="role"/> under any of the claim types
    /// <see cref="GetRoleValues"/> reads, compared case-insensitively.
    /// </summary>
    /// <param name="principal">The principal to read; a null principal holds no role.</param>
    /// <param name="role">The role name to look for.</param>
    public static bool HasRole(this ClaimsPrincipal? principal, string role) =>
        !string.IsNullOrEmpty(role)
        && principal.GetRoleValues().Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the principal carries an <see cref="AuthClaimTypes.Permission"/> claim granting
    /// <paramref name="permission"/> outright, independently of the roles it holds.
    /// </summary>
    /// <remarks>
    /// SECURITY: this is the framework's ONE definition of "the token itself grants this
    /// permission". Both authorization gates read it, the HTTP policy handler and the CQRS pipeline
    /// gate, so a grant the minting host stored and emitted as a claim cannot be honored at one
    /// boundary and denied at the other: in a multi-service deployment the claim is the only way
    /// such a grant reaches a service that does not own the grant table. It is additive, never a
    /// denial, so a principal without the claim still passes on a role the registry grants.
    /// </remarks>
    /// <param name="principal">The principal to read; a null principal holds no permission.</param>
    /// <param name="permission">The permission name to look for, compared ordinally.</param>
    /// <returns><see langword="true"/> when the principal carries a matching claim.</returns>
    public static bool HasPermissionClaim(this ClaimsPrincipal? principal, string permission) =>
        !string.IsNullOrEmpty(permission)
        && principal is not null
        && principal.HasClaim(AuthClaimTypes.Permission, permission);

    /// <summary>
    /// Returns the refresh-session identifier the token was minted for (the <c>sid</c> claim), or
    /// <see langword="null"/> when the principal carries none or carries an unparsable value.
    /// </summary>
    /// <remarks>
    /// Null is an ordinary answer, not an error: tokens issued before <c>sid</c> shipped carry no
    /// such claim, and every reader treats its absence as "the caller's own session is unknown" (a
    /// device list simply marks no row as current). Nothing authenticates on this value.
    /// </remarks>
    /// <param name="principal">The principal to read; a null principal yields null.</param>
    public static Guid? FindSessionId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(AuthClaimTypes.SessionId)?.Value;
        return Guid.TryParse(value, CultureInfo.InvariantCulture, out var sessionId) ? sessionId : null;
    }

    /// <summary>
    /// Whether the token behind this principal was minted after a second authentication factor was
    /// presented, that is whether it carries an <see cref="AuthClaimTypes.MultiFactor"/> claim.
    /// </summary>
    /// <remarks>
    /// SECURITY: presence is the whole test, and absence denies rather than falling back to a role
    /// check. Only the framework's sign-in flow stamps the claim, and only after a code verified, so
    /// a caller cannot reach a multi-factor-gated use case with a token minted before the account
    /// turned two-factor on.
    /// </remarks>
    /// <param name="principal">The principal to read; a null principal has no second factor.</param>
    /// <returns><see langword="true"/> when the principal carries the claim.</returns>
    public static bool HasMultiFactor(this ClaimsPrincipal? principal) =>
        principal?.FindFirst(AuthClaimTypes.MultiFactor) is not null;

    /// <summary>
    /// Returns the method that satisfied the second-factor challenge (the value of the
    /// <see cref="AuthClaimTypes.MultiFactor"/> claim), or <see langword="null"/> when the principal
    /// carries no such claim.
    /// </summary>
    /// <param name="principal">The principal to read; a null principal yields null.</param>
    /// <returns>The method name, or <see langword="null"/>.</returns>
    public static string? FindMultiFactorMethod(this ClaimsPrincipal? principal) =>
        principal?.FindFirst(AuthClaimTypes.MultiFactor)?.Value;
}
