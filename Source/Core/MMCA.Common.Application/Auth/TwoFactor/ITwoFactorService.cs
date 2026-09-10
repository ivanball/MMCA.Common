using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.Auth.TwoFactor;

/// <summary>
/// The cryptography behind the time-based second factor: minting a shared secret, rendering it as a
/// provisioning URI, verifying a code inside the configured skew window, and generating and matching
/// single-use recovery codes.
/// </summary>
/// <remarks>
/// Deliberately stateless and persistence-free. Everything it needs arrives as an argument and
/// nothing it produces is written anywhere, so an app can call it directly (a data migration
/// enrolling existing accounts, say) without dragging in a store, and every method is trivially
/// testable against a fixed clock.
/// </remarks>
public interface ITwoFactorService
{
    /// <summary>
    /// Mints a fresh shared secret for one account, Base32 encoded the way authenticator apps expect.
    /// </summary>
    /// <returns>The Base32 secret. Show it to the account owner once and store it, never log it.</returns>
    string GenerateSecret();

    /// <summary>
    /// Builds the <c>otpauth://totp/...</c> URI an authenticator app is keyed from, usually rendered
    /// as a QR code.
    /// </summary>
    /// <param name="secret">The Base32 secret from <see cref="GenerateSecret"/>.</param>
    /// <param name="accountName">
    /// The label identifying the account inside the issuer, normally the user's email address.
    /// </param>
    /// <returns>The provisioning URI.</returns>
    [SuppressMessage(
        "Design",
        "CA1055:URI-return values should not be strings",
        Justification = "An otpauth:// provisioning value is produced verbatim: round-tripping it through System.Uri re-normalizes the percent-encoding of the issuer:account label, which is exactly the part an authenticator app parses. Callers render it into a QR code as text.")]
    string BuildProvisioningUri(string secret, string accountName);

    /// <summary>
    /// Verifies a code against the secret, accepting the configured number of time steps on each side
    /// of the current one.
    /// </summary>
    /// <param name="secret">The account's Base32 secret.</param>
    /// <param name="code">The code the caller presented; null, empty and malformed all fail.</param>
    /// <returns><see langword="true"/> when the code is valid inside the window.</returns>
    bool VerifyCode(string secret, string? code);

    /// <summary>
    /// Generates a fresh set of single-use recovery codes: the plaintext to show the user once, and
    /// the hashes to store.
    /// </summary>
    /// <returns>The generated set. The plaintext exists only in the returned value.</returns>
    RecoveryCodeSet GenerateRecoveryCodes();

    /// <summary>
    /// Hashes one recovery code the way the store keeps it, so a caller can look a presented code up
    /// without the store ever seeing plaintext.
    /// </summary>
    /// <param name="code">The plaintext recovery code.</param>
    /// <returns>The hash.</returns>
    string HashRecoveryCode(string code);

    /// <summary>
    /// Finds the stored hash matching a presented recovery code, comparing in fixed time.
    /// </summary>
    /// <param name="code">The code the caller presented.</param>
    /// <param name="storedHashes">The account's unspent recovery-code hashes.</param>
    /// <param name="matchedHash">The hash that matched, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when one of the stored hashes matched.</returns>
    bool TryMatchRecoveryCode(string? code, IReadOnlyCollection<string> storedHashes, out string? matchedHash);
}

/// <summary>
/// A freshly generated set of recovery codes: the plaintext to hand to the user exactly once, and the
/// hashes paired with it positionally for storage.
/// </summary>
/// <remarks>
/// The two lists travel together so a caller cannot store one set and display another. Nothing here
/// is persisted by the service, and the plaintext is expected to be dropped as soon as it has been
/// shown.
/// </remarks>
/// <param name="Codes">The plaintext codes, in generation order.</param>
/// <param name="Hashes">The hash of each code, at the same index.</param>
public sealed record RecoveryCodeSet(
    IReadOnlyList<string> Codes,
    IReadOnlyList<string> Hashes);
