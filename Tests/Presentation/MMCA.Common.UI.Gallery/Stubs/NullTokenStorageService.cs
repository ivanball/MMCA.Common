using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// In-memory-empty <see cref="ITokenStorageService"/>: there is no stored session in the gallery.
/// Exists so the <c>AuthDelegatingHandler</c> registered by <c>AddUIShared</c> resolves cleanly
/// (it is never actually invoked because no API calls are made).
/// </summary>
internal sealed class NullTokenStorageService : ITokenStorageService
{
    public Task<string?> GetAccessTokenAsync() => Task.FromResult<string?>(null);

    public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);

    public Task SetTokensAsync(string accessToken, string refreshToken) => Task.CompletedTask;

    public Task ClearTokensAsync() => Task.CompletedTask;
}
