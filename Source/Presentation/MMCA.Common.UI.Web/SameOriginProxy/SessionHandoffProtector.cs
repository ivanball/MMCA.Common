using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// Wraps tokens that must cross the page on their way between the Blazor Server circuit and the
/// HttpOnly session cookies. The circuit lives in this process but can only reach the browser's
/// cookie jar through a script <c>fetch</c>, so instead of handing the script a token it hands it this
/// ciphertext: encrypted and signed with the host's data-protection key ring, bound to one purpose
/// each way (an access-token handoff can never be replayed as a cookie write), and expired after
/// <see cref="Lifetime"/>. A script can carry it but can neither read a token out of it nor present
/// it to any API.
/// </summary>
internal sealed class SessionHandoffProtector(IDataProtectionProvider dataProtectionProvider)
{
    /// <summary>How long a handoff stays redeemable: one fetch plus one interop hop.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private const string PurposeRoot = "MMCA.Common.UI.Web.SameOriginProxy.SessionHandoff";

    private readonly ITimeLimitedDataProtector _accessToken =
        dataProtectionProvider.CreateProtector(PurposeRoot, "AccessToken").ToTimeLimitedDataProtector();

    private readonly ITimeLimitedDataProtector _tokenPair =
        dataProtectionProvider.CreateProtector(PurposeRoot, "TokenPair").ToTimeLimitedDataProtector();

    public string ProtectAccessToken(string accessToken) => _accessToken.Protect(accessToken, Lifetime);

    public string? UnprotectAccessToken(string? handoff) => TryUnprotect(_accessToken, handoff);

    public string ProtectTokenPair(string accessToken, string refreshToken) =>
        _tokenPair.Protect(JsonSerializer.Serialize(new TokenPair(accessToken, refreshToken)), Lifetime);

    public (string AccessToken, string RefreshToken)? UnprotectTokenPair(string? handoff)
    {
        var json = TryUnprotect(_tokenPair, handoff);
        if (json is null)
        {
            return null;
        }

        try
        {
            var pair = JsonSerializer.Deserialize<TokenPair>(json);
            return pair is null || string.IsNullOrWhiteSpace(pair.AccessToken) || string.IsNullOrWhiteSpace(pair.RefreshToken)
                ? null
                : (pair.AccessToken, pair.RefreshToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryUnprotect(ITimeLimitedDataProtector protector, string? handoff)
    {
        if (string.IsNullOrWhiteSpace(handoff))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(handoff);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Tampered, expired, minted for the other purpose, or under a retired key: no handoff.
            return null;
        }
    }

    private sealed record TokenPair(string AccessToken, string RefreshToken);
}
