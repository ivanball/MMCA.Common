using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Covers <see cref="JwtTokenGenerator.ConfigureInProcessTokenValidation"/>: the shared replacement for the
/// per-repo <c>InProcessJwtBearer</c> helpers. The behaviour that matters is that JWKS/OIDC discovery is
/// switched off and the static committed key takes over, because a test host that still tries to discover
/// fails at the first authenticated request with a network error rather than an auth error.
/// </summary>
public class JwtTokenGeneratorTests
{
    private const string Audience = "TestAudience";

    private static JwtBearerOptions ConfiguredOptions(string audience = Audience)
    {
        var options = new JwtBearerOptions
        {
            Authority = "https://identity.example.com",
            RequireHttpsMetadata = true,
            ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                "https://identity.example.com/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever()),
        };

        JwtTokenGenerator.ConfigureInProcessTokenValidation(options, audience);
        return options;
    }

    [Fact]
    public void ConfigureInProcessTokenValidation_StopsJwksDiscovery()
    {
        var options = ConfiguredOptions();

        options.Authority.Should().BeNull();
        options.ConfigurationManager.Should().BeNull();
        options.RequireHttpsMetadata.Should().BeFalse();
    }

    [Fact]
    public void ConfigureInProcessTokenValidation_ValidatesAgainstTheCommittedTestKey()
    {
        var options = ConfiguredOptions();

        options.TokenValidationParameters.ValidateIssuerSigningKey.Should().BeTrue();
        options.TokenValidationParameters.IssuerSigningKey.Should().BeOfType<RsaSecurityKey>()
            .Which.KeyId.Should().Be(JwtTokenGenerator.DefaultKeyId);
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        options.TokenValidationParameters.ValidIssuer.Should().Be(JwtTokenGenerator.DefaultIssuer);
    }

    [Fact]
    public void ConfigureInProcessTokenValidation_TakesTheAudienceFromTheCaller()
    {
        var options = ConfiguredOptions("SomeOtherApi");

        options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        options.TokenValidationParameters.ValidAudience.Should().Be("SomeOtherApi");
    }

    [Fact]
    public void ConfigureInProcessTokenValidation_AcceptsATokenFromGenerateToken()
    {
        var options = ConfiguredOptions();
        var token = JwtTokenGenerator.GenerateToken(Audience, userId: 42, role: "Admin");

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, options.TokenValidationParameters, out _);

        principal.FindFirst(ClaimTypes.Role)?.Value.Should().Be("Admin");
        principal.FindFirst("user_id")?.Value.Should().Be("42");
    }

    [Fact]
    public void ConfigureInProcessTokenValidation_RejectsATokenForAnotherAudience()
    {
        var options = ConfiguredOptions();
        var token = JwtTokenGenerator.GenerateToken("AnotherApi", userId: 42, role: "Admin");

        var validate = () => new JwtSecurityTokenHandler().ValidateToken(token, options.TokenValidationParameters, out _);

        validate.Should().Throw<SecurityTokenInvalidAudienceException>();
    }

    [Fact]
    public void ConfigureInProcessTokenValidation_RejectsANullOptionsArgument()
    {
        var configure = () => JwtTokenGenerator.ConfigureInProcessTokenValidation(null!, Audience);

        configure.Should().Throw<ArgumentNullException>();
    }
}
