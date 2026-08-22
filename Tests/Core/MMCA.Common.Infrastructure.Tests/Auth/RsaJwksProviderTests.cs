using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies <see cref="RsaJwksProvider"/> returns an empty key set when JWKS publishing is
/// disabled (the default), and materializes a single RSA JWK with the configured key id and
/// use/algorithm metadata when an inline PEM is provided.
/// </summary>
public sealed class RsaJwksProviderTests
{
    [Fact]
    public void GetJsonWebKeySet_WhenDisabled_ReturnsEmpty()
    {
        // Arrange
        var settings = new JwksSettings { Enabled = false };
        var sut = new RsaJwksProvider(Options.Create(settings));

        // Act
        var keySet = sut.GetJsonWebKeySet();

        // Assert
        keySet.Keys.Should().BeEmpty();
    }

    [Fact]
    public void GetJsonWebKeySet_WhenEnabledWithoutKeyMaterial_ReturnsEmpty()
    {
        // Arrange: Enabled=true but neither RsaPublicKeyPem nor RsaPublicKeyPath set.
        var settings = new JwksSettings { Enabled = true };
        var sut = new RsaJwksProvider(Options.Create(settings));

        // Act
        var keySet = sut.GetJsonWebKeySet();

        // Assert
        keySet.Keys.Should().BeEmpty();
    }

    [Fact]
    public void GetJsonWebKeySet_WhenEnabledWithInlinePem_ReturnsRsaJwk()
    {
        // Arrange: generate a real RSA key pair and export the public key as PEM.
        using var rsa = RSA.Create(2048);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        var settings = new JwksSettings
        {
            Enabled = true,
            KeyId = "test-key-1",
            RsaPublicKeyPem = publicPem,
        };
        var sut = new RsaJwksProvider(Options.Create(settings));

        // Act
        var keySet = sut.GetJsonWebKeySet();

        // Assert
        keySet.Keys.Should().ContainSingle();
        var jwk = keySet.Keys[0];
        jwk.Kty.Should().Be("RSA");
        jwk.Kid.Should().Be("test-key-1");
        jwk.Use.Should().Be("sig");
        jwk.Alg.Should().Be("RS256");
        jwk.N.Should().NotBeNullOrEmpty("modulus must be exported");
        jwk.E.Should().NotBeNullOrEmpty("exponent must be exported");
    }

    [Fact]
    public void GetJsonWebKeySet_WhenGivenAPrivateKeyPem_ExportsOnlyThePublicParameters()
    {
        // Arrange: a misconfiguration that hands the provider the PRIVATE half. ImportFromPem accepts
        // it, so the only thing standing between that mistake and a signing key published on
        // /.well-known/jwks.json is ExportParameters(includePrivateParameters: false).
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();

        var settings = new JwksSettings
        {
            Enabled = true,
            KeyId = "private-key-1",
            RsaPublicKeyPem = privatePem,
        };
        var sut = new RsaJwksProvider(Options.Create(settings));

        // Act
        var keySet = sut.GetJsonWebKeySet();

        // Assert
        keySet.Keys.Should().ContainSingle();
        var jwk = keySet.Keys[0];
        jwk.N.Should().NotBeNullOrEmpty("the modulus is public and must still be published");
        jwk.E.Should().NotBeNullOrEmpty("the exponent is public and must still be published");
        jwk.D.Should().BeNullOrEmpty("the private exponent must never leave the process");
        jwk.P.Should().BeNullOrEmpty("the first prime factor must never leave the process");
        jwk.Q.Should().BeNullOrEmpty("the second prime factor must never leave the process");
        jwk.DP.Should().BeNullOrEmpty("the first CRT exponent must never leave the process");
        jwk.DQ.Should().BeNullOrEmpty("the second CRT exponent must never leave the process");
        jwk.QI.Should().BeNullOrEmpty("the CRT coefficient must never leave the process");
    }

    [Fact]
    public void GetJsonWebKeySet_IsCached_RepeatedCallsReturnSameInstance()
    {
        // Arrange
        var settings = new JwksSettings { Enabled = false };
        var sut = new RsaJwksProvider(Options.Create(settings));

        // Act
        var first = sut.GetJsonWebKeySet();
        var second = sut.GetJsonWebKeySet();

        // Assert: provider caches via Lazy<T>; same instance both times.
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void GetJsonWebKeySet_TransientKeyFileFailure_IsNotCached_AndALaterCallSucceeds()
    {
        // A transient IO failure reading the PEM used to be cached forever by the default
        // Lazy mode, bricking /.well-known/jwks.json (and cross-service auth) until a restart.
        var path = Path.Combine(Path.GetTempPath(), $"mmca-jwks-{Guid.NewGuid():N}.pem");
        var settings = new JwksSettings
        {
            Enabled = true,
            KeyId = "transient-key",
            RsaPublicKeyPath = path,
        };
        var sut = new RsaJwksProvider(Options.Create(settings));

        try
        {
            // Act 1: the key file is not there yet.
            var firstCall = () => sut.GetJsonWebKeySet();
            firstCall.Should().Throw<FileNotFoundException>();

            // Act 2: the file appears (the transient condition clears).
            using var rsa = RSA.Create(2048);
            File.WriteAllText(path, rsa.ExportSubjectPublicKeyInfoPem());

            // Assert: the failure was not cached, so the retry builds the key set.
            var keySet = sut.GetJsonWebKeySet();
            keySet.Keys.Should().ContainSingle();
            keySet.Keys[0].Kid.Should().Be("transient-key");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
