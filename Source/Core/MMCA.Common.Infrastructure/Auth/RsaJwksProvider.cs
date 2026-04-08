using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// <see cref="IJwksProvider"/> implementation that materializes a <see cref="JsonWebKeySet"/>
/// from a PEM-encoded RSA public key, configured via <see cref="JwksSettings"/>. When JWKS
/// publishing is disabled (the default), or when no key material is configured, the provider
/// returns an empty key set so the endpoint remains queryable.
/// </summary>
/// <param name="options">The bound <see cref="JwksSettings"/> options.</param>
public sealed class RsaJwksProvider(IOptions<JwksSettings> options) : IJwksProvider
{
    private readonly Lazy<JsonWebKeySet> _cachedKeySet = new(() => BuildKeySet(options.Value));

    /// <inheritdoc />
    public JsonWebKeySet GetJsonWebKeySet() => _cachedKeySet.Value;

    private static JsonWebKeySet BuildKeySet(JwksSettings settings)
    {
        if (!settings.Enabled)
        {
            return new JsonWebKeySet();
        }

        var pem = ResolvePem(settings);
        if (string.IsNullOrWhiteSpace(pem))
        {
            return new JsonWebKeySet();
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        var rsaSecurityKey = new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false))
        {
            KeyId = settings.KeyId,
        };

        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(rsaSecurityKey);
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;

        var keySet = new JsonWebKeySet();
        keySet.Keys.Add(jwk);
        return keySet;
    }

    private static string? ResolvePem(JwksSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RsaPublicKeyPem))
        {
            return settings.RsaPublicKeyPem;
        }

        if (!string.IsNullOrWhiteSpace(settings.RsaPublicKeyPath))
        {
            // File.ReadAllText is acceptable here — the provider runs once at startup
            // (the result is cached in _cachedKeySet) so we don't need an async read path.
            return File.ReadAllText(settings.RsaPublicKeyPath);
        }

        return null;
    }
}
