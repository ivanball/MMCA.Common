using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// <see cref="ITokenRefresher"/> that never has a session to refresh. The gallery has no API to
/// refresh against; the refresher is registered only to keep the DI graph complete.
/// </summary>
internal sealed class NullTokenRefresher : ITokenRefresher
{
    public Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
