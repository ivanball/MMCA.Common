using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Configuration for the JWKS (JSON Web Key Set) endpoint exposed by the Identity service.
/// Bound from the <c>Jwks</c> configuration section. When <see cref="Enabled"/> is
/// <see langword="false"/> (the default), <c>RsaJwksProvider</c> resolves to an empty key set
/// and the <c>/.well-known/jwks.json</c> endpoint returns <c>{"keys":[]}</c>.
/// <para>
/// To enable JWKS publishing, set <see cref="Enabled"/> to <see langword="true"/> and provide
/// either an inline <see cref="RsaPublicKeyPem"/> value or a path via <see cref="RsaPublicKeyPath"/>.
/// The <see cref="KeyId"/> is published as the JWK <c>kid</c> claim and must match the
/// <c>kid</c> header of any token signed by the Identity service.
/// </para>
/// </summary>
public sealed class JwksSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Jwks";

    /// <summary>
    /// Gets a value indicating whether JWKS publishing is enabled. Defaults to <see langword="false"/>
    /// so existing HMAC-only deployments do not start advertising an RSA key set by accident.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the key identifier exposed as the <c>kid</c> claim. Tokens signed by the Identity
    /// service must include this same <c>kid</c> in their JWT header so consumers can pick the
    /// correct public key from the JWKS document.
    /// </summary>
    [StringLength(64)]
    public string KeyId { get; init; } = "default";

    /// <summary>
    /// Gets the inline PEM-encoded RSA public key. Mutually exclusive with
    /// <see cref="RsaPublicKeyPath"/>. Use <see cref="RsaPublicKeyPath"/> when the key is too
    /// large to inline in configuration or when it is mounted as a Kubernetes secret.
    /// </summary>
    public string? RsaPublicKeyPem { get; init; }

    /// <summary>
    /// Gets the absolute path to a PEM-encoded RSA public-key file. Mutually exclusive with
    /// <see cref="RsaPublicKeyPem"/>.
    /// </summary>
    public string? RsaPublicKeyPath { get; init; }
}
