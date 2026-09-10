using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// What starting two-factor enrollment hands back: the shared secret to key an authenticator app
/// with, in both of the forms an app can consume it.
/// </summary>
/// <remarks>
/// SECURITY: the secret is returned exactly once, to the authenticated account owner, and the second
/// factor is NOT active yet. It becomes active only once a code minted from this secret is presented
/// back, which is what proves the authenticator was really keyed and stops an account locking itself
/// out of its own sign-in.
/// </remarks>
/// <param name="SharedKey">The Base32 secret, for an authenticator app that is keyed by typing.</param>
/// <param name="ProvisioningUri">
/// The <c>otpauth://totp/...</c> URI the same secret is rendered as a QR code from.
/// </param>
[SuppressMessage(
    "Design",
    "CA1054:URI-like parameters should not be strings",
    Justification = "An otpauth:// provisioning value is carried verbatim: round-tripping it through System.Uri re-normalizes the percent-encoding of the issuer:account label, which is exactly the part an authenticator app parses. It is also serialized straight into a QR code as text.")]
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "An otpauth:// provisioning value is carried verbatim: round-tripping it through System.Uri re-normalizes the percent-encoding of the issuer:account label, which is exactly the part an authenticator app parses. It is also serialized straight into a QR code as text.")]
public readonly record struct TwoFactorSetupResponse(
    string SharedKey,
    string ProvisioningUri);

/// <summary>
/// The single-use recovery codes for an account, returned when enrollment is confirmed and whenever
/// the codes are regenerated.
/// </summary>
/// <remarks>
/// SECURITY: this is the ONLY time the plaintext codes exist outside the user's hands. The store keeps
/// only their hashes (the password-reset token design), so a lost list cannot be re-read and can only
/// be replaced. Regenerating always replaces the whole set, never appends to it.
/// </remarks>
/// <param name="RecoveryCodes">The plaintext codes, in the order they were generated.</param>
public sealed record TwoFactorRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
