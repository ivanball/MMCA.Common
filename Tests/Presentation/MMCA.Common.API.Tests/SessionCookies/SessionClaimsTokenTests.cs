using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// <see cref="SessionClaimsToken"/>: the browser-facing copy of an access token keeps every claim and
/// the expiry (so the client can render its signed-in state) but carries no signature, so a validator
/// that requires signed tokens, as every API does, refuses it.
/// </summary>
public sealed class SessionClaimsTokenTests
{
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("a-test-signing-key-that-is-long-enough-for-hs256"));

    [Fact]
    public void Create_KeepsTheClaimsAndExpiry_ButDropsTheSignature()
    {
        var expires = DateTime.UtcNow.AddMinutes(10);
        var accessToken = CreateSignedJwt(expires);

        var claimsToken = SessionClaimsToken.Create(accessToken);

        claimsToken.Should().NotBeNull().And.EndWith(".", "an unsecured JWT has an empty signature segment");
        claimsToken.Should().NotBe(accessToken);
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(claimsToken);
        parsed.Header.Alg.Should().Be("none");
        parsed.Claims.Should().Contain(c => c.Type == "email" && c.Value == "ada@example.com");
        parsed.Claims.Should().Contain(c => c.Value == "Admin");
        parsed.ValidTo.Should().BeCloseTo(expires, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_TheResultIsRejectedByASignatureValidatingApi()
    {
        var claimsToken = SessionClaimsToken.Create(CreateSignedJwt(DateTime.UtcNow.AddMinutes(10)));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "mmca-tests",
            ValidAudience = "mmca-api",
            IssuerSigningKey = Key,
        };

        var act = () => new JwtSecurityTokenHandler().ValidateToken(claimsToken, parameters, out _);

        act.Should().Throw<SecurityTokenException>("the claims token must not authorize anything");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public void Create_ReturnsNullForAnythingThatIsNotAJwt(string? value) =>
        SessionClaimsToken.Create(value).Should().BeNull();

    private static string CreateSignedJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "mmca-tests",
            audience: "mmca-api",
            claims: [new Claim("email", "ada@example.com"), new Claim(ClaimTypes.Role, "Admin")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires,
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
}
