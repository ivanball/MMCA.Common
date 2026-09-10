using System.Security.Cryptography;

namespace MMCA.Common.Testing.Aspire.Preconditions;

/// <summary>
/// A throwaway RSA-2048 keypair in PEM form, plus the two environment-variable names the framework's
/// <c>WithE2eRsaKeys()</c> AppHost extension forwards to an Identity resource.
/// <para>
/// This closes the other half of the precondition problem. An Identity host normally takes its RS256
/// signing key from user-secrets locally and from Key Vault in production; a CI runner has neither,
/// so the placeholder in <c>appsettings.json</c> reaches the JwtBearer options factory, which throws
/// on the FIRST request. Every request then answers 500, including the liveness probe, so the
/// resource never turns healthy and the dependency graph waits out its budget on what looks like a
/// network problem. Minting a keypair before the AppHost is built removes the whole failure mode, and
/// costs one key generation per collection.
/// </para>
/// </summary>
/// <param name="PrivateKeyPem">The PKCS#8 private key, PEM encoded.</param>
/// <param name="PublicKeyPem">The SubjectPublicKeyInfo public key, PEM encoded.</param>
public sealed record EphemeralRsaKeyPair(string PrivateKeyPem, string PublicKeyPem)
{
    /// <summary>
    /// Environment variable carrying the private key. <c>WithE2eRsaKeys()</c> maps it onto
    /// <c>Jwt__RsaPrivateKeyPem</c>.
    /// </summary>
    public const string PrivateKeyVariable = "E2E_JWT_PRIVATE_KEY_PEM";

    /// <summary>
    /// Environment variable carrying the public key. <c>WithE2eRsaKeys()</c> maps it onto both
    /// <c>Jwt__RsaPublicKeyPem</c> and <c>Jwks__RsaPublicKeyPem</c>.
    /// </summary>
    public const string PublicKeyVariable = "E2E_JWT_PUBLIC_KEY_PEM";

    /// <summary>
    /// Key size. 2048 rather than 4096 on purpose: this key lives for one test collection, and key
    /// generation is on the critical path of every AppHost boot that needs one.
    /// </summary>
    public const int KeySizeInBits = 2048;

    /// <summary>Generates a fresh keypair.</summary>
    /// <returns>The generated keypair.</returns>
    public static EphemeralRsaKeyPair Create()
    {
        using var rsa = RSA.Create(KeySizeInBits);
        return new EphemeralRsaKeyPair(rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }
}
