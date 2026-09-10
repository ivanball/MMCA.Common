namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The two-factor surface an Identity module's <c>User</c> aggregate exposes to the shared sign-in
/// and enrollment workflows: the shared secret, whether the second factor is active, and the hashes
/// of the account's unspent recovery codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only on purpose.</b> Nothing here mutates. Enrollment, disabling and recovery-code
/// redemption all go through <c>ITwoFactorStore</c>, so the aggregate keeps its own factory methods
/// and invariants and the framework never reaches past them. This contract exists so the workflows
/// can ASK about the account without knowing the app's <c>User</c> type.
/// </para>
/// <para>
/// <b>Hashes, never codes.</b> <see cref="TwoFactorRecoveryCodeHashes"/> holds digests produced by
/// <c>ITwoFactorService.HashRecoveryCode</c>, the same hash-at-rest treatment the password-reset
/// tokens get. A store that kept the plaintext codes would hand out working second factors to anyone
/// who could read the table.
/// </para>
/// <para>
/// <b>A secret alone is not an enabled second factor.</b> Starting enrollment writes
/// <see cref="TwoFactorSecret"/> while <see cref="IsTwoFactorEnabled"/> stays false, so an
/// abandoned enrollment can never gate a later sign-in.
/// </para>
/// </remarks>
public interface ITwoFactorUserState
{
    /// <summary>Whether the account's second factor is active and therefore demanded at sign-in.</summary>
    bool IsTwoFactorEnabled { get; }

    /// <summary>
    /// The Base32 shared secret the account's codes are derived from, or <see langword="null"/> when
    /// no enrollment has been started.
    /// </summary>
    string? TwoFactorSecret { get; }

    /// <summary>
    /// The hashes of the account's unspent recovery codes. Empty when the account has none left,
    /// which is a state the user recovers from by regenerating rather than an error.
    /// </summary>
    IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes { get; }
}
