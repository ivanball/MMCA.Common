namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Platform-agnostic JWT token persistence. Each host provides an implementation using the safe
/// mechanism for its platform: browser hosts (Blazor Server/WASM) hold the access token in memory and
/// mirror the refresh token to an HttpOnly cookie (never localStorage); MAUI uses OS SecureStorage.
/// </summary>
public interface ITokenStorageService
{
    /// <summary>Retrieves the stored JWT access token, or <see langword="null"/> if none exists.</summary>
    Task<string?> GetAccessTokenAsync();

    /// <summary>Retrieves the stored refresh token, or <see langword="null"/> if none exists.</summary>
    Task<string?> GetRefreshTokenAsync();

    /// <summary>Persists both tokens after a successful login or token refresh.</summary>
    Task SetTokensAsync(string accessToken, string refreshToken);

    /// <summary>Removes both tokens (used on logout).</summary>
    Task ClearTokensAsync();
}
