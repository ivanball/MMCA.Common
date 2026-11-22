namespace MMCA.Common.Domain.Auth.Credentials;

/// <summary>
/// The one definition of "this account holds a password credential", shared by password sign-in and
/// the sign-in-link workflow. An account with no password (the shape an external-login account
/// carries, ADR-036) has an empty hash and an empty salt, and password sign-in never accepts it.
/// </summary>
internal static class PasswordCredential
{
    /// <summary>Whether the account carries stored password material (a non-empty hash AND salt).</summary>
    /// <param name="user">The account.</param>
    /// <returns><see langword="true"/> when password sign-in is a path the stored material allows.</returns>
    internal static bool IsStored(IAuthUser user) =>
        user.PasswordHash.Length > 0 && user.PasswordSalt.Length > 0;
}
