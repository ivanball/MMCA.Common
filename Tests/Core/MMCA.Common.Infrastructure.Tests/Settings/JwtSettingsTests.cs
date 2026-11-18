using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Infrastructure.Tests.Settings;

public class JwtSettingsTests
{
    [Fact]
    public void SectionName_IsJwt() =>
        JwtSettings.SectionName.Should().Be("Jwt");

    // SigningAlgorithm: asymmetric signing by default, so a validator verifies without holding the
    // key that mints tokens, and a host that never sets Jwt:SigningAlgorithm gets the algorithm an
    // extracted service needs.
    [Theory]
    [InlineData(nameof(JwtSettings.SecretForKey), "")]
    [InlineData(nameof(JwtSettings.Issuer), "")]
    [InlineData(nameof(JwtSettings.Audience), "")]
    [InlineData(nameof(JwtSettings.AccessTokenExpirationMinutes), 15)]
    [InlineData(nameof(JwtSettings.RefreshTokenExpirationDays), 7)]
    [InlineData(nameof(JwtSettings.SigningAlgorithm), JwtSigningAlgorithm.RS256)]
    public void Default_PropertyHasTheDocumentedValue(string property, object? expected) =>
        typeof(JwtSettings).GetProperty(property)!.GetValue(new JwtSettings()).Should().Be(expected);

    [Fact]
    public void Validate_OnTheDefaults_DemandsAnRsaPrivateKey()
    {
        var sut = new JwtSettings { Issuer = "https://issuer.example.com", Audience = "api" };

        sut.Validate(new ValidationContext(sut))
            .Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(JwtSettings.RsaPrivateKeyPem));
    }

    [Fact]
    public void Validate_WithHS256AndAShortSecret_DemandsALongerSecret()
    {
        var sut = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = "too-short",
            Issuer = "https://issuer.example.com",
            Audience = "api",
        };

        sut.Validate(new ValidationContext(sut))
            .Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(JwtSettings.SecretForKey));
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new JwtSettings
        {
            SecretForKey = "super-secret-key-that-is-32-chars!",
            Issuer = "https://issuer.example.com",
            Audience = "https://audience.example.com",
            AccessTokenExpirationMinutes = 60,
            RefreshTokenExpirationDays = 14,
        };

        sut.SecretForKey.Should().Be("super-secret-key-that-is-32-chars!");
        sut.Issuer.Should().Be("https://issuer.example.com");
        sut.Audience.Should().Be("https://audience.example.com");
        sut.AccessTokenExpirationMinutes.Should().Be(60);
        sut.RefreshTokenExpirationDays.Should().Be(14);
    }

    // L183: a zero or negative lifetime must fail validation (and so host startup under
    // ValidateOnStart), not surface later as a 500 on every sign-in or refresh.
    [Theory]
    [InlineData(0, 7, nameof(JwtSettings.AccessTokenExpirationMinutes))]
    [InlineData(-5, 7, nameof(JwtSettings.AccessTokenExpirationMinutes))]
    [InlineData(15, 0, nameof(JwtSettings.RefreshTokenExpirationDays))]
    [InlineData(15, -1, nameof(JwtSettings.RefreshTokenExpirationDays))]
    public void Validation_RejectsANonPositiveTokenLifetime(int accessMinutes, int refreshDays, string member)
    {
        var sut = new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = "super-secret-key-that-is-32-chars!",
            Issuer = "https://issuer.example.com",
            Audience = "api",
            AccessTokenExpirationMinutes = accessMinutes,
            RefreshTokenExpirationDays = refreshDays,
        };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(sut, new ValidationContext(sut), results, validateAllProperties: true)
            .Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(member));
    }
}
