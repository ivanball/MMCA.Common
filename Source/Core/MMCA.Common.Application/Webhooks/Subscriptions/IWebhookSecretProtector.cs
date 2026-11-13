using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Webhooks.Subscriptions;

/// <summary>
/// Protects signing secrets at rest. The default uses the host's data-protection key ring, so a
/// multi-replica host needs the shared key ring. Registered by <c>AddWebhooks(configuration)</c>.
/// </summary>
public interface IWebhookSecretProtector
{
    /// <summary>Protects a plaintext secret for storage.</summary>
    /// <param name="secret">The plaintext secret.</param>
    /// <returns>The protected form.</returns>
    string Protect(string secret);

    /// <summary>Recovers the plaintext secret; never throws.</summary>
    /// <param name="protectedSecret">The protected form.</param>
    /// <returns>The plaintext secret, or <c>Webhooks.SecretUnreadable</c>.</returns>
    Result<string> Unprotect(string protectedSecret);
}
