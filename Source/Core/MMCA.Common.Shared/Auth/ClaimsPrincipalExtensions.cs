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
}
