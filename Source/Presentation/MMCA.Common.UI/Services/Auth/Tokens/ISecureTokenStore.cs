namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// Raw token persistence, with no freshness semantics: it reads back exactly what was written and
/// never triggers a refresh. It exists to split storage from the freshness-checking layer above it,
/// which is what keeps the MAUI wiring acyclic. <see cref="ITokenStorageService"/> (the layer callers
/// consume) depends on <see cref="ITokenRefresher"/>; the refresher in turn depends on THIS interface,
/// so the graph is storage to refresher to raw store, with no loop and no re-entrancy hazard at
/// runtime.
/// <para>
/// Only hosts that persist tokens themselves implement it: MAUI backs it with OS SecureStorage. The
/// browser hosts hold the access token in memory and keep the refresh token in an HttpOnly cookie, so
/// they have no raw store to expose.
/// </para>
/// </summary>
public interface ISecureTokenStore
{
    /// <summary>Reads the stored access token verbatim, or <see langword="null"/> if none exists.</summary>
    Task<string?> GetAccessTokenAsync();

    /// <summary>Reads the stored refresh token verbatim, or <see langword="null"/> if none exists.</summary>
    Task<string?> GetRefreshTokenAsync();

    /// <summary>Persists both tokens, replacing whatever was stored before.</summary>
    Task SetTokensAsync(string accessToken, string refreshToken);

    /// <summary>Removes both tokens (used on logout).</summary>
    Task ClearTokensAsync();
}
