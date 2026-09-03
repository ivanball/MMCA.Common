namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Selects which algorithm <c>TokenService</c> uses to sign access tokens (and which key
/// type the JWT bearer middleware uses to validate them). The choice encodes the deployment
/// shape, not a compatibility level, which is why both values stay:
/// <para>
/// <see cref="RS256"/> (the default, <c>JwtSettings.SigningAlgorithm</c>) is what an extracted
/// service topology needs. The Identity service signs with its RSA private key
/// (<c>RsaPrivateKeyPem</c>) and every other service validates against the JWKS endpoint exposing
/// <c>RsaPublicKeyPem</c>, so no peer ever holds the signing key. It is also the correct choice for a
/// monolith that intends to extract later, because the token format does not change when it does.
/// </para>
/// <para>
/// <see cref="HS256"/> is for a single-process monolith whose issuer and validators all live in the
/// one host: they can share the symmetric key in <c>JwtSettings.SecretForKey</c> and skip RSA key
/// management entirely. Switching a running deployment between the two invalidates every existing
/// token (a hard cutover).
/// </para>
/// </summary>
public enum JwtSigningAlgorithm
{
    /// <summary>HMAC-SHA256 using a shared symmetric key (<c>SecretForKey</c>); the single-host monolith choice.</summary>
    HS256 = 0,

    /// <summary>RSA-SHA256 using an asymmetric key pair (<c>RsaPrivateKeyPem</c> + <c>RsaPublicKeyPem</c>); the default.</summary>
    RS256 = 1,
}
