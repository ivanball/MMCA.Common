using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class TokenServiceTests : IDisposable
{
    private static readonly string Base64Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    // HS256 is stated rather than inherited: RS256 is the default, so a settings object meant to
    // exercise the symmetric path selects it explicitly.
    private static readonly JwtSettings Settings = new()
    {
        SigningAlgorithm = JwtSigningAlgorithm.HS256,
        SecretForKey = Base64Secret,
        Issuer = "https://test-issuer",
        Audience = "test-audience",
        AccessTokenExpirationMinutes = 30,
        RefreshTokenExpirationDays = 7
    };

    private readonly TokenService _sut = new(Options.Create(Settings));

    public void Dispose() => _sut.Dispose();

    // ── GenerateAccessToken ──
    [Fact]
    public void GenerateAccessToken_ReturnsValidJwt()
    {
        var token = _sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var handler = new JwtSecurityTokenHandler();
        handler.CanReadToken(token).Should().BeTrue();

        var jwt = handler.ReadJwtToken(token);
        jwt.Issuer.Should().Be(Settings.Issuer);
        jwt.Audiences.Should().Contain(Settings.Audience);
        jwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Email && c.Value == "user@test.com");
        jwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Organizer");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "1");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Jti);
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Iat);
    }

    [Fact]
    public void GenerateAccessToken_SetsCorrectExpiration()
    {
        var before = DateTime.UtcNow;
        var token = _sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");
        var after = DateTime.UtcNow;

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.ValidTo.Should().BeAfter(before.AddMinutes(Settings.AccessTokenExpirationMinutes - 1));
        jwt.ValidTo.Should().BeBefore(after.AddMinutes(Settings.AccessTokenExpirationMinutes + 1));
    }

    [Fact]
    public void GenerateAccessToken_WithAdditionalClaims_IncludesThem()
    {
        var speakerId = Guid.NewGuid();
        var additionalClaims = new[] { new Claim("speaker_id", speakerId.ToString()) };
        var token = _sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User", additionalClaims);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "speaker_id" && c.Value == speakerId.ToString());
    }

    [Fact]
    public void GenerateAccessToken_WithoutAdditionalClaims_OmitsExtraClaims()
    {
        var token = _sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().NotContain(c => c.Type == "speaker_id");
    }

    // ── GenerateRefreshToken ──
    [Fact]
    public void GenerateRefreshToken_ReturnsValidBase64()
    {
        var token = _sut.GenerateRefreshToken();

        var act = () => Convert.FromBase64String(token);
        act.Should().NotThrow();
        Convert.FromBase64String(token).Should().HaveCount(64);
    }

    [Fact]
    public void GenerateRefreshToken_ReturnsDifferentTokensOnEachCall()
    {
        var token1 = _sut.GenerateRefreshToken();
        var token2 = _sut.GenerateRefreshToken();

        token1.Should().NotBe(token2);
    }

    // ── Token lifetimes ──
    [Fact]
    public void Lifetimes_DeriveFromJwtSettings()
    {
        var settings = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = Base64Secret,
            Issuer = "https://test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 45,
            RefreshTokenExpirationDays = 10
        };
        using var service = new TokenService(Options.Create(settings));

        service.AccessTokenLifetime.Should().Be(TimeSpan.FromMinutes(45));
        service.RefreshTokenLifetime.Should().Be(TimeSpan.FromDays(10));
    }

    // ── GetPrincipalFromExpiredToken ──
    [Fact]
    public void GetPrincipalFromExpiredToken_ValidToken_ReturnsPrincipal()
    {
        var token = _sut.GenerateAccessToken(42, "user@test.com", "Attendee", "Test Attendee");

        var principal = _sut.GetPrincipalFromExpiredToken(token);

        principal.Should().NotBeNull();
        // JwtSecurityTokenHandler maps the inbound `sub` onto NameIdentifier, which is exactly
        // why every framework reader goes through ClaimsPrincipalExtensions, not one claim name.
        principal!.GetUserId().Should().Be(42);
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_InvalidToken_ReturnsNull()
    {
        var result = _sut.GetPrincipalFromExpiredToken("not-a-jwt-token");

        result.Should().BeNull();
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_TokenWithWrongIssuer_ReturnsNull()
    {
        var wrongSettings = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = Base64Secret,
            Issuer = "https://wrong-issuer",
            Audience = Settings.Audience,
            AccessTokenExpirationMinutes = 30
        };
        using var wrongService = new TokenService(Options.Create(wrongSettings));
        var token = wrongService.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var result = _sut.GetPrincipalFromExpiredToken(token);

        result.Should().BeNull();
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_TokenWithWrongSigningKey_ReturnsNull()
    {
        var differentSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var wrongSettings = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = differentSecret,
            Issuer = Settings.Issuer,
            Audience = Settings.Audience,
            AccessTokenExpirationMinutes = 30
        };
        using var wrongService = new TokenService(Options.Create(wrongSettings));
        var token = wrongService.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var result = _sut.GetPrincipalFromExpiredToken(token);

        result.Should().BeNull();
    }

    // ── RS256 path ──
    private static (string PrivatePem, string PublicPem) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static IOptions<JwtSettings> CreateRsaSettings(string privatePem, string? publicPem = null) =>
        Options.Create(new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.RS256,
            RsaPrivateKeyPem = privatePem,
            RsaPublicKeyPem = publicPem,
            Issuer = "https://test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 30,
            RefreshTokenExpirationDays = 7,
        });

    [Fact]
    public void Constructor_Rs256_WithoutPrivateKey_Throws()
    {
        var settings = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.RS256,
            Issuer = "https://test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 30,
        };

        var act = () => new TokenService(Options.Create(settings));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RsaPrivateKeyPem is required*");
    }

    [Fact]
    public void Constructor_Hs256_WithoutSecretForKey_Throws()
    {
        var settings = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = string.Empty,
            Issuer = "https://test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 30,
        };

        var act = () => new TokenService(Options.Create(settings));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SecretForKey is required*");
    }

    [Fact]
    public void GenerateAccessToken_Rs256_ProducesRs256Header()
    {
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem));

        var token = sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        jwt.Issuer.Should().Be("https://test-issuer");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "1");
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Rs256_RoundTripsValidToken()
    {
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem));

        var token = sut.GenerateAccessToken(42, "user@test.com", "Attendee", "Test Attendee");
        var principal = sut.GetPrincipalFromExpiredToken(token);

        principal.Should().NotBeNull();
        // JwtSecurityTokenHandler maps the inbound `sub` onto NameIdentifier, which is exactly
        // why every framework reader goes through ClaimsPrincipalExtensions, not one claim name.
        principal!.GetUserId().Should().Be(42);
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Rs256_RejectsHs256Token()
    {
        // An attacker who learns the public key must not be able to forge a token by signing
        // it with HS256 using the public key as the symmetric secret. The validator pins
        // ValidAlgorithms = [RsaSha256] so HS256 tokens are rejected even if the bytes match.
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        using var rsaService = new TokenService(CreateRsaSettings(privatePem, publicPem));

        var hmacSettings = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = Base64Secret,
            Issuer = "https://test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 30,
        };
        using var hmacService = new TokenService(Options.Create(hmacSettings));
        var hmacToken = hmacService.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var principal = rsaService.GetPrincipalFromExpiredToken(hmacToken);

        principal.Should().BeNull();
    }

    // ── sub is the only identifier claim ──
    [Fact]
    public void GenerateAccessToken_EmitsNoDuplicateUserIdClaim()
    {
        var token = _sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().NotContain(
            c => string.Equals(c.Type, "user_id", StringComparison.Ordinal),
            "two claims carrying one identity can disagree, and every reader then has to know both names");
        jwt.Claims.Should().ContainSingle(c => c.Type == JwtRegisteredClaimNames.Sub);
    }

    // ── kid ──
    [Fact]
    public void GenerateAccessToken_Rs256_StampsTheJwksKeyIdOnTheHeader()
    {
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        var jwks = new JwksSettings { Enabled = true, KeyId = "identity-2026-07", RsaPublicKeyPem = publicPem };
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem), null, Options.Create(jwks));

        var token = sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Kid.Should().Be("identity-2026-07");
    }

    // The point of the kid is that it names a key in the document the JWKS endpoint publishes. A
    // mismatch between the two is invisible until a cross-service validator cannot resolve the key.
    [Fact]
    public void GenerateAccessToken_Rs256_KeyIdMatchesTheKeyThePublishedJwksAdvertises()
    {
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        var jwks = new JwksSettings { Enabled = true, KeyId = "identity-2026-07", RsaPublicKeyPem = publicPem };
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem), null, Options.Create(jwks));
        var provider = new RsaJwksProvider(Options.Create(jwks));

        var jwt = new JwtSecurityTokenHandler()
            .ReadJwtToken(sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User"));

        var published = provider.GetJsonWebKeySet().Keys.Should().ContainSingle().Subject;
        jwt.Header.Kid.Should().Be(published.Kid);
    }

    [Fact]
    public void GenerateAccessToken_Hs256_EmitsNoKeyId()
    {
        var token = _sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Kid.Should().BeNull("a symmetric deployment publishes no key set to select from");
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Rs256_StillValidatesAKidCarryingToken()
    {
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        var jwks = new JwksSettings { Enabled = true, KeyId = "identity-2026-07", RsaPublicKeyPem = publicPem };
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem), null, Options.Create(jwks));

        var token = sut.GenerateAccessToken(42, "user@test.com", "Attendee", "Test Attendee");

        sut.GetPrincipalFromExpiredToken(token).Should().NotBeNull("the refresh flow is one of the validation paths");
    }

    // The in-process validator (AddCommonAuthentication) builds its RsaSecurityKey from the public
    // PEM alone, so it carries no key id. A token that now names one must still validate against it,
    // or every deployed API would start rejecting its own issuer's tokens.
    [Fact]
    public void AKidCarryingToken_ValidatesAgainstAKeyWithNoKeyId()
    {
        var (privatePem, publicPem) = GenerateRsaKeyPair();
        var jwks = new JwksSettings { Enabled = true, KeyId = "identity-2026-07", RsaPublicKeyPem = publicPem };
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem), null, Options.Create(jwks));
        var token = sut.GenerateAccessToken(42, "user@test.com", "Attendee", "Test Attendee");

        using var validationRsa = RSA.Create();
        validationRsa.ImportFromPem(publicPem);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "https://test-issuer",
            ValidAudience = "test-audience",
            IssuerSigningKey = new RsaSecurityKey(validationRsa),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rs256_WithoutExplicitPublicKey_DerivesFromPrivateKey()
    {
        // When RsaPublicKeyPem is omitted, the service derives the public parameters from the
        // private key so the issuer can still self-validate (refresh-token flow).
        var (privatePem, _) = GenerateRsaKeyPair();
        using var sut = new TokenService(CreateRsaSettings(privatePem, publicPem: null));

        var token = sut.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");
        var principal = sut.GetPrincipalFromExpiredToken(token);

        principal.Should().NotBeNull();
    }
}
