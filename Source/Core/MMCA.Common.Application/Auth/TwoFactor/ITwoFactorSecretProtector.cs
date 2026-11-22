using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.TwoFactor;

/// <summary>
/// Protects an account's two-factor shared secret at rest. The enrollment workflow protects the
/// freshly minted secret before it reaches <see cref="ITwoFactorStore"/>, and the shipped
/// <see cref="ITwoFactorAuthenticator"/> unprotects the stored value when it verifies a code, so the
/// app's store only ever holds the protected form. The default uses the host's data-protection key
/// ring, so a multi-replica host needs the shared key ring. Registered by
/// <c>AddTwoFactorAuthentication(configuration)</c>.
/// </summary>
public interface ITwoFactorSecretProtector
{
    /// <summary>Protects a plaintext Base32 secret for storage.</summary>
    /// <param name="secret">The plaintext secret.</param>
    /// <returns>The protected form.</returns>
    string Protect(string secret);

    /// <summary>Recovers the plaintext secret; never throws.</summary>
    /// <param name="protectedSecret">The protected form, as the store holds it.</param>
    /// <returns>The plaintext secret, or a failure when this host's key ring cannot read it.</returns>
    Result<string> Unprotect(string protectedSecret);
}
