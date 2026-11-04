using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace MMCA.Common.Infrastructure.Auth;

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
/// <para>
/// <see cref="AdditionalKeys"/> publishes further public keys after the current one (the next key
/// staged for a rotation, or the previous key while its tokens are still live). Signing always
/// uses the current key; every local validator accepts all of them.
/// </para>
/// </summary>
public sealed class JwksSettings : IValidatableObject
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

    /// <summary>
    /// Gets the public keys published after the current one and accepted by every local validator,
    /// none of which is ever used to sign. Empty by default. Each <see cref="JwksAdditionalKey.KeyId"/>
    /// must differ from <see cref="KeyId"/> and from every other entry, and every entry must load
    /// (checked at startup).
    /// </summary>
    /// <remarks>
    /// Removing a key stops it validating on different schedules: in-process validators
    /// (<c>AddCommonAuthentication</c>, the refresh flow in <c>TokenService</c>) build their key list
    /// once, so the removal takes effect when the process restarts; forwarded validators
    /// (<c>AddForwardedJwtBearer</c>) drop it when they next refresh the JWKS document, on the
    /// JWT bearer handler's metadata refresh interval (the library default unless the host changes it).
    /// </remarks>
    public IReadOnlyList<JwksAdditionalKey> AdditionalKeys { get; init; } = [];

    /// <summary>
    /// Validates <see cref="AdditionalKeys"/>, which property-level annotations do not reach: every
    /// entry needs a key id of at most 64 characters and key material, and no key id may repeat
    /// (including the current <see cref="KeyId"/>), since a validator selects a key by its id.
    /// </summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>The validation failures, if any.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { KeyId };

        for (var i = 0; i < AdditionalKeys.Count; i++)
        {
            var key = AdditionalKeys[i];
            var member = string.Create(CultureInfo.InvariantCulture, $"{nameof(AdditionalKeys)}[{i}]");

            if (string.IsNullOrWhiteSpace(key.KeyId) || key.KeyId.Length > 64)
            {
                yield return new ValidationResult(
                    $"{member}.{nameof(JwksAdditionalKey.KeyId)} is required and must be at most 64 characters.",
                    [nameof(AdditionalKeys)]);
            }
            else if (!seen.Add(key.KeyId))
            {
                yield return new ValidationResult(
                    $"{member}.{nameof(JwksAdditionalKey.KeyId)} '{key.KeyId}' duplicates another JWKS key id; every published key needs a distinct kid.",
                    [nameof(AdditionalKeys)]);
            }

            if (string.IsNullOrWhiteSpace(key.RsaPublicKeyPem) && string.IsNullOrWhiteSpace(key.RsaPublicKeyPath))
            {
                yield return new ValidationResult(
                    $"{member} has no key material: set {nameof(JwksAdditionalKey.RsaPublicKeyPem)} or {nameof(JwksAdditionalKey.RsaPublicKeyPath)}.",
                    [nameof(AdditionalKeys)]);
            }
            else if (LoadFailure(key) is { } failure)
            {
                // Parsed here so ValidateOnStart fails the host at startup, rather than sign-in,
                // refresh and every authenticated request failing at first use of the key set.
                yield return new ValidationResult(
                    $"{member} (kid '{key.KeyId}') cannot be loaded: {failure}",
                    [nameof(AdditionalKeys)]);
            }
        }
    }

    /// <summary>
    /// Loads an additional key the way every validator will (resolve the PEM or read the path,
    /// import it) and reports why that failed, or <see langword="null"/> when it loads.
    /// </summary>
    /// <param name="key">The additional key.</param>
    /// <returns>The failure message, or <see langword="null"/>.</returns>
    private static string? LoadFailure(JwksAdditionalKey key)
    {
        try
        {
            _ = key.CreateValidationKey();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException or NotSupportedException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Builds the validation keys for <see cref="AdditionalKeys"/>, in order. Used by every local
    /// validator so a token signed with any published key validates.
    /// </summary>
    /// <returns>One public key per additional key, stamped with its key id.</returns>
    public IReadOnlyList<RsaSecurityKey> CreateAdditionalValidationKeys() =>
        [.. AdditionalKeys.Select(key => key.CreateValidationKey())];
}

/// <summary>
/// A public key published in the JWKS document alongside the current signing key
/// (<see cref="JwksSettings.AdditionalKeys"/>), and accepted by every local validator. Nothing is
/// ever signed with it.
/// <para>
/// This is what makes a signing-key rotation free of a 401 wave: stage the next key here before
/// switching to it (so validators that cache the JWKS document already know it), switch, then keep
/// the previous key here until every token it signed has expired.
/// </para>
/// </summary>
/// <remarks>
/// Key material follows the same rules as the current key on <see cref="JwksSettings"/>: an inline
/// <see cref="RsaPublicKeyPem"/> wins over <see cref="RsaPublicKeyPath"/>, and the file is read
/// when the key is first used, not at startup. Unlike the current key, an entry with neither is a
/// configuration error, since a listed key that publishes nothing can only be a mistake.
/// </remarks>
public sealed class JwksAdditionalKey
{
    /// <summary>
    /// Gets the key identifier published as the JWK <c>kid</c>. Must be unique across the current
    /// key and every additional key, since a validator selects the key by this value.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string KeyId { get; init; } = string.Empty;

    /// <summary>Gets the inline PEM-encoded RSA public key. Wins over <see cref="RsaPublicKeyPath"/>.</summary>
    public string? RsaPublicKeyPem { get; init; }

    /// <summary>Gets the absolute path to a PEM-encoded RSA public-key file.</summary>
    public string? RsaPublicKeyPath { get; init; }

    /// <summary>
    /// Builds the public validation key, stamped with <see cref="KeyId"/>. Only the public
    /// parameters are kept, so the key holds no <see cref="RSA"/> instance and needs no disposal,
    /// and a private PEM supplied by mistake still yields only the public half.
    /// </summary>
    /// <returns>The key a validator accepts tokens signed by.</returns>
    /// <exception cref="InvalidOperationException">Neither <see cref="RsaPublicKeyPem"/> nor <see cref="RsaPublicKeyPath"/> is set.</exception>
    public RsaSecurityKey CreateValidationKey()
    {
        var pem = JwksKeyMaterial.ResolvePem(RsaPublicKeyPem, RsaPublicKeyPath)
            ?? throw new InvalidOperationException(
                $"JWKS additional key '{KeyId}' has no key material: set RsaPublicKeyPem or RsaPublicKeyPath.");

        return JwksKeyMaterial.CreatePublicKey(pem, KeyId);
    }
}

/// <summary>Key-material rules shared by the current JWKS key and every additional key.</summary>
internal static class JwksKeyMaterial
{
    /// <summary>Returns the inline PEM when set, else the contents of the file at the path, else <see langword="null"/>.</summary>
    /// <param name="pem">The inline PEM.</param>
    /// <param name="path">The path to a PEM file.</param>
    /// <returns>The PEM text, or <see langword="null"/> when neither is configured.</returns>
    internal static string? ResolvePem(string? pem, string? path)
    {
        if (!string.IsNullOrWhiteSpace(pem))
        {
            return pem;
        }

        // A synchronous read is acceptable: every caller materializes keys once and caches them.
        return string.IsNullOrWhiteSpace(path) ? null : File.ReadAllText(path);
    }

    /// <summary>Imports a PEM and keeps only its public parameters, stamped with a key id.</summary>
    /// <param name="pem">An RSA PEM (public, or private by mistake: only the public half survives).</param>
    /// <param name="keyId">The <c>kid</c> to stamp on the key.</param>
    /// <returns>The public key.</returns>
    internal static RsaSecurityKey CreatePublicKey(string pem, string keyId)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false))
        {
            KeyId = keyId,
        };
    }
}
