using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// No-op <see cref="IAuthUIService"/> for the backend-less gallery. The gallery renders the real
/// Login/Register pages for a11y + render-smoke scanning only — no auth calls are exercised, so
/// every operation returns a benign default. Registered before <c>AddUIShared</c> so its
/// <c>TryAddScoped&lt;IAuthUIService, AuthUIService&gt;()</c> defers to this stub.
/// </summary>
internal sealed class NoOpAuthUIService : IAuthUIService
{
    public string? LastError => null;

    public Task<AuthenticationResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthenticationResponse?>(null);

    public Task<AuthenticationResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthenticationResponse?>(null);

    public Task<AuthenticationResponse?> ExchangeOAuthCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthenticationResponse?>(null);

    public Task LogoutAsync() => Task.CompletedTask;

    public Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
