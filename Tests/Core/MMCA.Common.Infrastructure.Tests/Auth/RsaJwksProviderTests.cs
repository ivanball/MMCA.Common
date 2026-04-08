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
}
