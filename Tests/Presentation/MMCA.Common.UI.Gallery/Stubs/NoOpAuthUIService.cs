using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// No-op <see cref="IAuthUIService"/> for the backend-less gallery. The gallery renders the real
/// Login/Register/Sessions pages for a11y + render-smoke scanning only: no auth calls are
/// exercised, so every operation returns a benign default. Registered before <c>AddUIShared</c> so
/// its <c>TryAddScoped&lt;IAuthUIService, AuthUIService&gt;()</c> defers to this stub.
/// </summary>
internal sealed class NoOpAuthUIService : IAuthUIService
{
    // Built from components rather than parsed: MA0176 rejects parsing a constant at runtime.
    private static readonly Guid CurrentDeviceSessionId =
        new(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11);

    private static readonly Guid OtherDeviceSessionId =
        new(0x22222222, 0x2222, 0x2222, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22);

    private static readonly Error Unavailable =
        Error.Failure("Gallery.AuthUnavailable", "The gallery has no backend.");

    public Task<Result<AuthenticationResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AuthenticationResponse>(Unavailable));

    public Task<Result<AuthenticationResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AuthenticationResponse>(Unavailable));

    public Task<Result<AuthenticationResponse>> ExchangeOAuthCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AuthenticationResponse>(Unavailable));

    public Task LogoutAsync() => Task.CompletedTask;

    public Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<Result> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable));

    // The Forgot Password page shows its confirmation regardless of this result (anti-enumeration),
    // so the gallery still renders the post-submit state for scanning.
    public Task<Result> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable));

    public Task<Result> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable));

    // Canned rows rather than an empty list: the axe scan has to reach the populated table, the
    // current-device chip and a live revoke button, which an empty state would hide. One row is
    // flagged current so both branches of the actions cell render.
    public Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RefreshSessionSummaryResponse> sessions =
        [
            new(
                SessionId: CurrentDeviceSessionId,
                CreatedAt: new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
                ExpiresAt: new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
                IpAddress: "203.0.113.7",
                UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                IsCurrent: true),
            new(
                SessionId: OtherDeviceSessionId,
                CreatedAt: new DateTime(2026, 1, 1, 18, 30, 0, DateTimeKind.Utc),
                ExpiresAt: new DateTime(2026, 1, 31, 18, 30, 0, DateTimeKind.Utc),
                IpAddress: null,
                UserAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Mobile/15E148 Safari/604.1",
                IsCurrent: false),
        ];

        return Task.FromResult(Result.Success(sessions));
    }

    public Task<Result> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable));
}
