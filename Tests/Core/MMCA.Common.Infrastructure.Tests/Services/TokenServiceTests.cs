using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class TokenServiceTests
{
    private static readonly string Base64Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    private static readonly JwtSettings Settings = new()
    {
        SecretForKey = Base64Secret,
        Issuer = "https://test-issuer",
        Audience = "test-audience",
        AccessTokenExpirationMinutes = 30,
        RefreshTokenExpirationDays = 7
    };

    private readonly TokenService _sut = new(Settings);

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
        jwt.Claims.Should().Contain(c => c.Type == "user_id" && c.Value == "1");
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

    // ── GetPrincipalFromExpiredToken ──
    [Fact]
    public void GetPrincipalFromExpiredToken_ValidToken_ReturnsPrincipal()
    {
        var token = _sut.GenerateAccessToken(42, "user@test.com", "Attendee", "Test Attendee");

        var principal = _sut.GetPrincipalFromExpiredToken(token);

        principal.Should().NotBeNull();
        principal!.Claims.Should().Contain(c => c.Type == "user_id" && c.Value == "42");
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
            SecretForKey = Base64Secret,
            Issuer = "https://wrong-issuer",
            Audience = Settings.Audience,
            AccessTokenExpirationMinutes = 30
        };
        var wrongService = new TokenService(wrongSettings);
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
            SecretForKey = differentSecret,
            Issuer = Settings.Issuer,
            Audience = Settings.Audience,
            AccessTokenExpirationMinutes = 30
        };
        var wrongService = new TokenService(wrongSettings);
        var token = wrongService.GenerateAccessToken(1, "user@test.com", "Organizer", "Test User");

        var result = _sut.GetPrincipalFromExpiredToken(token);

        result.Should().BeNull();
    }
}
