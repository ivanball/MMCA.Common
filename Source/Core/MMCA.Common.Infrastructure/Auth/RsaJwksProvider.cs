using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// <see cref="IJwksProvider"/> implementation that materializes a <see cref="JsonWebKeySet"/>
/// from PEM-encoded RSA public keys, configured via <see cref="JwksSettings"/>: the current key
/// first, then every <see cref="JwksSettings.AdditionalKeys"/> entry in order. When JWKS
/// publishing is disabled (the default), or when no current key material is configured, the
/// provider returns an empty key set so the endpoint remains queryable.
/// </summary>
/// <param name="options">The bound <see cref="JwksSettings"/> options.</param>
public sealed class RsaJwksProvider(IOptions<JwksSettings> options) : IJwksProvider
{
    // PublicationOnly, not the default ExecutionAndPublication: the default caches a factory
    // exception forever, so one transient IO failure reading the PEM would brick
    // /.well-known/jwks.json (and with it cross-service auth) until the process restarts.
    // PublicationOnly caches only a successful result and lets a later call retry. Concurrent
    // factory runs are harmless: BuildKeySet is pure and keeps no RSA instance.
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

        // The file is read on the first request, and the result is cached in _cachedKeySet on
        // success; a failure is not cached, so the next call reads the file again.
        var pem = JwksKeyMaterial.ResolvePem(settings.RsaPublicKeyPem, settings.RsaPublicKeyPath);
        if (string.IsNullOrWhiteSpace(pem))
        {
            return new JsonWebKeySet();
        }

        var keySet = new JsonWebKeySet();
        keySet.Keys.Add(ToJwk(JwksKeyMaterial.CreatePublicKey(pem, settings.KeyId)));

        // The current key stays first: signing never uses an additional key, and a validator that
        // ignores kid and takes the first key still gets the one tokens are signed with today.
        foreach (var additional in settings.CreateAdditionalValidationKeys())
        {
            keySet.Keys.Add(ToJwk(additional));
        }

        return keySet;
    }

    private static JsonWebKey ToJwk(RsaSecurityKey key)
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        return jwk;
    }
}
