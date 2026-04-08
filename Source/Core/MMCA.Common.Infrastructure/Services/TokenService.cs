using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// JWT token service that creates access tokens, refresh tokens, and validates expired tokens
/// for the refresh flow. Supports both symmetric (HMAC-SHA256) and asymmetric (RSA-SHA256)
/// signing, selected via <see cref="IJwtSettings.SigningAlgorithm"/>.
/// <para>
/// In monolith mode, the default <see cref="JwtSigningAlgorithm.HS256"/> reuses
/// <see cref="IJwtSettings.SecretForKey"/> (Base64-encoded HMAC key). In microservice mode,
/// <see cref="JwtSigningAlgorithm.RS256"/> signs with <see cref="IJwtSettings.RsaPrivateKeyPem"/>
/// and validates with <see cref="IJwtSettings.RsaPublicKeyPem"/>; other services validate
/// via the JWKS endpoint exposing the same public key.
/// </para>
/// </summary>
public sealed class TokenService : ITokenService, IDisposable
{
    private readonly IJwtSettings _jwtSettings;
    private readonly SigningCredentials _signingCredentials;
    private readonly SecurityKey _validationKey;
    private readonly string _validationAlgorithm;

    // Owned RSA instances kept alive for the service's lifetime. The RsaSecurityKey wrappers
    // hold weak references to these — disposing the service releases the underlying handles.
    private readonly RSA? _ownedSigningRsa;
    private readonly RSA? _ownedValidationRsa;

    /// <summary>
    /// Initializes the token service. The signing and validation keys are materialized once
    /// at construction time so subsequent token operations don't repeatedly parse the
    /// configured key material.
    /// </summary>
    /// <param name="jwtSettings">The bound JWT settings.</param>
    public TokenService(IJwtSettings jwtSettings)
    {
        ArgumentNullException.ThrowIfNull(jwtSettings);
        _jwtSettings = jwtSettings;

        if (jwtSettings.SigningAlgorithm == JwtSigningAlgorithm.RS256)
        {
            (_signingCredentials, _validationKey, _ownedSigningRsa, _ownedValidationRsa) =
                BuildRsaCredentials(jwtSettings);
            _validationAlgorithm = SecurityAlgorithms.RsaSha256;
        }
        else
        {
            (_signingCredentials, _validationKey) = BuildHmacCredentials(jwtSettings);
            _validationAlgorithm = SecurityAlgorithms.HmacSha256;
        }
    }

    /// <inheritdoc />
    public string GenerateAccessToken(
        UserIdentifierType userId,
        string email,
        string role,
        string fullName,
        IEnumerable<Claim>? additionalClaims = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("user_id", userId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, fullName),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role)
        };

        if (additionalClaims is not null)
        {
            claims.AddRange(additionalClaims);
        }

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            signingCredentials: _signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <inheritdoc />
    public string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(randomBytes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Validates everything except lifetime — this is intentional because the method's purpose
    /// is to extract claims from an expired access token during the refresh flow.
    /// The algorithm is explicitly verified to prevent token substitution attacks (an attacker
    /// cannot swap an HS256 token signed with the public key for an RS256 token).
    /// </remarks>
    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
#pragma warning disable CA5404 // ValidateLifetime=false is intentional - we need to read claims from expired tokens for refresh
            ValidateLifetime = false,
#pragma warning restore CA5404
            ValidIssuer = _jwtSettings.Issuer,
            ValidAudience = _jwtSettings.Audience,
            IssuerSigningKey = _validationKey,
            ValidAlgorithms = [_validationAlgorithm],
        };

        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);

            if (securityToken is not JwtSecurityToken jwtSecurityToken ||
                !jwtSecurityToken.Header.Alg.Equals(_validationAlgorithm, StringComparison.Ordinal))
            {
                return null;
            }

            return principal;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ownedSigningRsa?.Dispose();
        _ownedValidationRsa?.Dispose();
    }

    private static (SigningCredentials Signing, SecurityKey Validation) BuildHmacCredentials(IJwtSettings jwtSettings)
    {
        if (string.IsNullOrWhiteSpace(jwtSettings.SecretForKey))
        {
            throw new InvalidOperationException(
                "JwtSettings.SecretForKey is required when SigningAlgorithm is HS256.");
        }

        // SecretForKey is Base64-encoded to safely store arbitrary bytes in JSON/env config.
        var symmetricKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtSettings.SecretForKey));
        var signing = new SigningCredentials(symmetricKey, SecurityAlgorithms.HmacSha256);
        return (signing, symmetricKey);
    }

    private static (SigningCredentials Signing, SecurityKey Validation, RSA SigningRsa, RSA ValidationRsa) BuildRsaCredentials(
        IJwtSettings jwtSettings)
    {
        if (string.IsNullOrWhiteSpace(jwtSettings.RsaPrivateKeyPem))
        {
            throw new InvalidOperationException(
                "JwtSettings.RsaPrivateKeyPem is required when SigningAlgorithm is RS256.");
        }

        var signingRsa = RSA.Create();
        try
        {
            signingRsa.ImportFromPem(jwtSettings.RsaPrivateKeyPem);
            var signingKey = new RsaSecurityKey(signingRsa);
            var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

            // Validation key: prefer the configured public key. Fall back to deriving the
            // public key from the private key parameters so issuers without an explicit
            // RsaPublicKeyPem still self-validate (refresh-token flow).
            var validationRsa = RSA.Create();
            try
            {
                if (!string.IsNullOrWhiteSpace(jwtSettings.RsaPublicKeyPem))
                {
                    validationRsa.ImportFromPem(jwtSettings.RsaPublicKeyPem);
                }
                else
                {
                    var publicParameters = signingRsa.ExportParameters(includePrivateParameters: false);
                    validationRsa.ImportParameters(publicParameters);
                }

                var validationKey = new RsaSecurityKey(validationRsa);
                return (signingCredentials, validationKey, signingRsa, validationRsa);
            }
            catch
            {
                validationRsa.Dispose();
                throw;
            }
        }
        catch
        {
            signingRsa.Dispose();
            throw;
        }
    }
}
