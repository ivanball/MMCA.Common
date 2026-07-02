namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The credential/refresh-token surface an Identity module's <c>User</c> aggregate exposes to the
/// shared <c>AuthenticationServiceBase&lt;TUser&gt;</c> workflow (ADR-032 password material, BR-205/206
/// refresh-token rotation). Deliberately minimal: profile fields, roles, linked aggregates and claim
/// sources stay app-specific — the shared workflow reaches them only through its per-app hooks
/// (<c>CreateAccessToken</c>, <c>CreateUser</c>, ...), never through this contract.
/// </summary>
public interface IAuthUser
{
#pragma warning disable CA1819 // Properties should not return arrays — mirrors IPasswordHasher's byte[] material and the existing app User aggregates (EF-mapped varbinary columns).
    /// <summary>The PBKDF2/legacy password hash (ADR-032).</summary>
    byte[] PasswordHash { get; }

    /// <summary>The salt paired with <see cref="PasswordHash"/> (its length selects the verify algorithm, ADR-032).</summary>
    byte[] PasswordSalt { get; }
#pragma warning restore CA1819

    /// <summary>The currently-issued refresh token, or null when revoked/never issued.</summary>
    string? RefreshToken { get; }

    /// <summary>Expiry of <see cref="RefreshToken"/> (UTC).</summary>
    DateTime? RefreshTokenExpiry { get; }

    /// <summary>Rotates the stored refresh token (BR-205).</summary>
    void UpdateRefreshToken(string refreshToken, DateTime expiry);

    /// <summary>Revokes the stored refresh token, forcing re-authentication (BR-206/216).</summary>
    void RevokeRefreshToken();
}
