namespace MMCA.UI.Shared.Services.Auth;

/// <summary>
/// Platform-agnostic JWT token persistence. Each host provides an implementation that uses the
/// appropriate storage mechanism: browser localStorage (Server/WASM) or MAUI SecureStorage.
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
