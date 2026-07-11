using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Canned <see cref="ITokenStorageService"/> for UI HTTP-service tests: returns a fixed access token
/// (which the services attach as the Bearer header) without any platform storage.
/// <see cref="AccessTokenProvider"/> is mutable so a test can simulate storage failures (e.g. the
/// prerender window where JS interop is unavailable) by swapping in a throwing delegate.
/// <see cref="SetTokensAsync"/>/<see cref="ClearTokensAsync"/> mutate the canned values so
/// login/logout flows can be asserted via <see cref="AccessToken"/>/<see cref="RefreshToken"/>.
/// </summary>
public sealed class StubTokenStorageService : ITokenStorageService
{
    /// <summary>Creates the stub with the given canned tokens.</summary>
    /// <param name="accessToken">The canned access token (or <see langword="null"/> for an anonymous client).</param>
    /// <param name="refreshToken">The canned refresh token.</param>
    public StubTokenStorageService(string? accessToken = "test-token", string? refreshToken = "test-refresh-token")
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessTokenProvider = () => Task.FromResult(AccessToken);
    }

    /// <summary>The canned access token (updated by <see cref="SetTokensAsync"/>/<see cref="ClearTokensAsync"/>).</summary>
    public string? AccessToken { get; set; }

    /// <summary>The canned refresh token (updated by <see cref="SetTokensAsync"/>/<see cref="ClearTokensAsync"/>).</summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// The delegate backing <see cref="GetAccessTokenAsync"/>. Defaults to returning
    /// <see cref="AccessToken"/>; replace it to simulate failures (e.g. a delegate throwing
    /// <see cref="InvalidOperationException"/> to mimic prerender storage access).
    /// </summary>
    public Func<Task<string?>> AccessTokenProvider { get; set; }

    /// <inheritdoc />
    public Task<string?> GetAccessTokenAsync() => AccessTokenProvider();

    /// <inheritdoc />
    public Task<string?> GetRefreshTokenAsync() => Task.FromResult(RefreshToken);

    /// <inheritdoc />
    public Task SetTokensAsync(string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearTokensAsync()
    {
        AccessToken = null;
        RefreshToken = null;
        return Task.CompletedTask;
    }
}
