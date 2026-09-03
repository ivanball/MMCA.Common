using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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
    // PublicationOnly, not the default ExecutionAndPublication: the default caches a factory
    // exception forever, so one transient IO failure reading the PEM would brick
    // /.well-known/jwks.json (and with it cross-service auth) until the process restarts.
    // PublicationOnly caches only a successful result and lets a later call retry. Concurrent
    // factory runs are harmless: BuildKeySet is pure and disposes its own RSA instance.
    private readonly Lazy<JsonWebKeySet> _cachedKeySet =
        new(() => BuildKeySet(options.Value), LazyThreadSafetyMode.PublicationOnly);

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
            // File.ReadAllText is acceptable here: the provider runs on the first request and the
            // result is cached in _cachedKeySet on success, so we don't need an async read path.
            // A failure is not cached, so the next call reads the file again.
            return File.ReadAllText(settings.RsaPublicKeyPath);
        }

        return null;
    }
}
