using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// Mints and caches the HS256 service token described by <see cref="ServiceTokenSettings"/>:
/// <c>sub</c> and <c>svc</c> name this service, <c>iss</c> and <c>aud</c> come from the settings,
/// <c>iat</c> and <c>nbf</c> are the clock's now, <c>exp</c> is now plus
/// <see cref="ServiceTokenSettings.LifetimeMinutes"/>, and every token carries a fresh <c>jti</c>.
/// </summary>
/// <remarks>
/// Registered as a singleton by <c>AddServiceTokenClient</c>, so one token serves every outgoing
/// call until its last 60 seconds; a token that could expire mid-call is never handed out. The cache
/// is safe under concurrent calls: readers take the published token without locking, and only a
/// caller that finds it inside the refresh window takes the lock and mints (once, for everyone
/// waiting).
/// </remarks>
public sealed class ServiceTokenProvider : IServiceTokenProvider
{
    /// <summary>A token with this much lifetime left, or less, is replaced rather than reused.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);

    private readonly ServiceTokenSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly SigningCredentials _signingCredentials;
    private readonly Lock _gate = new();
    private CachedToken? _cached;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceTokenProvider"/> class.
    /// </summary>
    /// <param name="options">The validated service-token settings.</param>
    /// <param name="timeProvider">The clock that stamps and ages the tokens.</param>
    public ServiceTokenProvider(IOptions<ServiceTokenSettings> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _settings = options.Value;
        _timeProvider = timeProvider;
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(_settings.SigningKey)),
            SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    public string GetToken()
    {
        var now = _timeProvider.GetUtcNow();
        var cached = Volatile.Read(ref _cached);
        if (IsFresh(cached, now))
        {
            return cached.Value;
        }

        lock (_gate)
        {
            // Another caller may have minted while this one waited for the lock.
            cached = _cached;
            if (IsFresh(cached, now))
            {
                return cached.Value;
            }

            var minted = Mint(now);
            Volatile.Write(ref _cached, minted);
            return minted.Value;
        }
    }

    private static bool IsFresh([NotNullWhen(true)] CachedToken? cached, DateTimeOffset now) =>
        cached is not null && cached.ExpiresAt - now > RefreshWindow;

    private CachedToken Mint(DateTimeOffset now)
    {
        // JWT times are whole seconds; the cache ages the token by the exp actually written into it.
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var expires = issuedAt.AddMinutes(_settings.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, _settings.ServiceName),
            new(ServiceTokenDefaults.ServiceClaimType, _settings.ServiceName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new CachedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>A minted token and the instant it expires.</summary>
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
}
