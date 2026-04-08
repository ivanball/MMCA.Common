namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Selects which algorithm <c>TokenService</c> uses to sign access tokens (and which key
/// type the JWT bearer middleware uses to validate them).
/// <para>
/// <see cref="HS256"/> is the default for backwards compatibility — single-process
/// monolith deployments where issuer and validators all live in the same host can keep
/// using the symmetric key in <c>JwtSettings.SecretForKey</c>.
/// </para>
/// <para>
/// <see cref="RS256"/> is the target for the microservice extraction (Phase 1+). The
/// Identity service signs with its RSA private key (<c>RsaPrivateKeyPem</c>), other
/// services validate via the JWKS endpoint exposing <c>RsaPublicKeyPem</c>. Switching
/// from HS256 to RS256 invalidates all existing tokens (hard cutover).
/// </para>
/// </summary>
public enum JwtSigningAlgorithm
{
    /// <summary>HMAC-SHA256 using a shared symmetric key (<c>SecretForKey</c>). Default.</summary>
    HS256 = 0,

    /// <summary>RSA-SHA256 using an asymmetric key pair (<c>RsaPrivateKeyPem</c> + <c>RsaPublicKeyPem</c>).</summary>
    RS256 = 1,
}
