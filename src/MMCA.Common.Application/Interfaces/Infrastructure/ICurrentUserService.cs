using System.Security.Claims;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Provides access to the currently authenticated user's identity and claims,
/// extracted from the HTTP context's JWT token.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>Gets the current user's claims principal.</summary>
    ClaimsPrincipal User { get; }

    /// <summary>Gets the current user's identifier, or <see langword="null"/> if unauthenticated.</summary>
    UserIdentifierType? UserId { get; }

    /// <summary>Gets the current user's role (e.g. "Organizer", "Attendee"), or <see langword="null"/> if unauthenticated.</summary>
    string? Role { get; }

    /// <summary>
    /// Extracts a typed claim value from the current user's claims.
    /// Useful for module-specific claims (e.g. speaker_id) without coupling Common to specific modules.
    /// </summary>
    /// <typeparam name="T">The target value type that implements <see cref="IParsable{TSelf}"/>.</typeparam>
    /// <param name="claimType">The claim type name to look up.</param>
    /// <returns>The parsed value, or <see langword="null"/> if the claim is missing or unparseable.</returns>
    T? GetClaimValue<T>(string claimType)
        where T : struct, IParsable<T>;
}
