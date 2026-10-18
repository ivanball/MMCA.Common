using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Grpc.Authentication;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Shared keys, settings and a hand-rolled HS256 minter for the service-token tests. The minter is
/// independent of <see cref="ServiceTokenProvider"/> on purpose: the negative cases (wrong key,
/// missing <c>svc</c>, expired) need tokens the provider would never produce.
/// </summary>
internal static class ServiceTokenTestData
{
    public const string ServiceName = "conference";

    public const string Issuer = "mmca-service";

    public const string Audience = "internal-grpc";

    public const string SectionName = "Grpc:ServiceToken";

    /// <summary>A fresh 32-byte key, Base64 encoded the way the settings expect it.</summary>
    public static string NewBase64Key(int byteCount = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));

    public static ServiceTokenSettings ValidSettings(string signingKey, int lifetimeMinutes = 5) => new()
    {
        SigningKey = signingKey,
        ServiceName = ServiceName,
        LifetimeMinutes = lifetimeMinutes,
    };

    public static Dictionary<string, string?> ConfigurationFor(string signingKey) => new(StringComparer.Ordinal)
    {
        [$"{SectionName}:SigningKey"] = signingKey,
        [$"{SectionName}:ServiceName"] = ServiceName,
    };

    public static TokenValidationParameters ValidationParameters(string signingKey) => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
    };

    /// <summary>Mints an HS256 token with the given shape; every argument is a knob a negative case turns.</summary>
    public static string Mint(
        string signingKey,
        string issuer = Issuer,
        string audience = Audience,
        bool includeServiceClaim = true,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("sub", ServiceName) };
        if (includeServiceClaim)
        {
            claims.Add(new Claim("svc", ServiceName));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = notBefore ?? now,
            NotBefore = notBefore ?? now,
            Expires = expires ?? now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
