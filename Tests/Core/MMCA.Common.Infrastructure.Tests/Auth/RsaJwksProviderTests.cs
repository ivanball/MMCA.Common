using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Infrastructure.Auth;

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

    // ── Additional keys (rotation) ──
    [Fact]
    public void GetJsonWebKeySet_WithAnAdditionalKey_PublishesBothKidsWithTheCurrentKeyFirst()
    {
        using var current = RSA.Create(2048);
        using var previous = RSA.Create(2048);
        var settings = new JwksSettings
        {
            Enabled = true,
            KeyId = "identity-2026-11",
            RsaPublicKeyPem = current.ExportSubjectPublicKeyInfoPem(),
            AdditionalKeys =
            [
                new JwksAdditionalKey { KeyId = "identity-2026-07", RsaPublicKeyPem = previous.ExportSubjectPublicKeyInfoPem() },
            ],
        };

        var keySet = new RsaJwksProvider(Options.Create(settings)).GetJsonWebKeySet();

        keySet.Keys.Select(k => k.Kid).Should().Equal("identity-2026-11", "identity-2026-07");
        keySet.Keys.Should().OnlyContain(k => k.Use == "sig" && k.Alg == "RS256" && string.IsNullOrEmpty(k.D));
        keySet.Keys[1].N.Should().Be(
            Base64UrlEncoder.Encode(previous.ExportParameters(false).Modulus),
            "the additional entry publishes the previous key's own modulus");
    }

    [Fact]
    public void GetJsonWebKeySet_WithAnAdditionalKeyFromAFile_ReadsThePath()
    {
        using var current = RSA.Create(2048);
        using var previous = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"mmca-jwks-prev-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, previous.ExportSubjectPublicKeyInfoPem());

        try
        {
            var settings = new JwksSettings
            {
                Enabled = true,
                KeyId = "current",
                RsaPublicKeyPem = current.ExportSubjectPublicKeyInfoPem(),
                AdditionalKeys = [new JwksAdditionalKey { KeyId = "previous", RsaPublicKeyPath = path }],
            };

            new RsaJwksProvider(Options.Create(settings)).GetJsonWebKeySet()
                .Keys.Select(k => k.Kid).Should().Equal("current", "previous");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_WithADuplicateKeyId_Fails()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var settings = new JwksSettings
        {
            Enabled = true,
            KeyId = "current",
            RsaPublicKeyPem = pem,
            AdditionalKeys =
            [
                new JwksAdditionalKey { KeyId = "previous", RsaPublicKeyPem = pem },
                new JwksAdditionalKey { KeyId = "previous", RsaPublicKeyPem = pem },
                new JwksAdditionalKey { KeyId = "current", RsaPublicKeyPem = pem },
            ],
        };

        var errors = Validate(settings);

        errors.Should().HaveCount(2, "the repeated additional kid and the one that shadows the current kid are both rejected");
        errors.Should().OnlyContain(e => e.ErrorMessage!.Contains("duplicates", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithAnAdditionalKeyMissingItsIdOrMaterial_Fails()
    {
        using var rsa = RSA.Create(2048);
        var settings = new JwksSettings
        {
            AdditionalKeys =
            [
                new JwksAdditionalKey { KeyId = string.Empty, RsaPublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem() },
                new JwksAdditionalKey { KeyId = "no-material" },
            ],
        };

        var errors = Validate(settings);

        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.ErrorMessage!.Contains("KeyId is required", StringComparison.Ordinal));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("no key material", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithAMalformedPem_FailsNamingTheKid()
    {
        var settings = new JwksSettings
        {
            AdditionalKeys = [new JwksAdditionalKey { KeyId = "previous", RsaPublicKeyPem = "-----BEGIN PUBLIC KEY-----\nnot a key\n-----END PUBLIC KEY-----" }],
        };

        Validate(settings).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("kid 'previous'").And.Contain("cannot be loaded");
    }

    [Fact]
    public void Validate_WithAMissingKeyFile_FailsNamingTheKid()
    {
        var settings = new JwksSettings
        {
            AdditionalKeys = [new JwksAdditionalKey { KeyId = "previous", RsaPublicKeyPath = Path.Combine(Path.GetTempPath(), $"mmca-missing-{Guid.NewGuid():N}.pem") }],
        };

        Validate(settings).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("kid 'previous'").And.Contain("cannot be loaded");
    }

    [Fact]
    public void Validate_WithDistinctLoadableAdditionalKeys_Passes()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"mmca-jwks-valid-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, rsa.ExportSubjectPublicKeyInfoPem());

        try
        {
            var settings = new JwksSettings
            {
                KeyId = "current",
                AdditionalKeys = [new JwksAdditionalKey { KeyId = "previous", RsaPublicKeyPath = path }],
            };

            Validate(settings).Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BoundFromConfiguration_ADuplicateKidFailsOptionsValidation()
    {
        // The same registration AddInfrastructure makes (Bind + ValidateDataAnnotations), so the
        // check runs at startup through ValidateOnStart rather than on the first JWKS request.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwks:KeyId"] = "current",
                ["Jwks:AdditionalKeys:0:KeyId"] = "previous",
                ["Jwks:AdditionalKeys:0:RsaPublicKeyPath"] = "/keys/a.pem",
                ["Jwks:AdditionalKeys:1:KeyId"] = "previous",
                ["Jwks:AdditionalKeys:1:RsaPublicKeyPath"] = "/keys/b.pem",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<JwksSettings>()
            .Bind(configuration.GetSection(JwksSettings.SectionName))
            .ValidateDataAnnotations();
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<JwksSettings>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*duplicates*");
    }

    private static List<ValidationResult> Validate(JwksSettings settings)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results;
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
