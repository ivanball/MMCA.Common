namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The credential surface an Identity module's <c>User</c> aggregate exposes to the shared
/// <c>AuthenticationServiceBase&lt;TUser&gt;</c> workflow (ADR-032 password material). Deliberately
/// minimal: profile fields, roles, linked aggregates and claim sources stay app-specific. The shared
/// workflow reaches them only through its per-app hooks (<c>CreateAccessToken</c>, <c>CreateUser</c>,
/// ...), never through this contract.
/// <para>
/// Refresh tokens are deliberately absent. They used to live here as a single plaintext column per
/// user, which capped every account at one signed-in device and put a usable credential in the users
/// table. They are now rows in <see cref="RefreshSession"/>, hashed at rest and reached through
/// <c>IRefreshSessionStore</c>, so this contract covers passwords only.
/// </para>
/// </summary>
public interface IAuthUser
{
#pragma warning disable CA1819 // Properties should not return arrays — mirrors IPasswordHasher's byte[] material and the existing app User aggregates (EF-mapped varbinary columns).
    /// <summary>The PBKDF2/legacy password hash (ADR-032).</summary>
    byte[] PasswordHash { get; }

    /// <summary>The salt paired with <see cref="PasswordHash"/> (its length selects the verify algorithm, ADR-032).</summary>
    byte[] PasswordSalt { get; }
#pragma warning restore CA1819
}
