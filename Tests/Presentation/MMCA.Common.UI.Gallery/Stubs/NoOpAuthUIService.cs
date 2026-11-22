using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// No-op <see cref="IAuthUIService"/> for the backend-less gallery. The gallery renders the real
/// Login/Register/Sessions pages for a11y + render-smoke scanning only: no auth calls are
/// exercised, so every operation returns a benign default. Registered before <c>AddUIShared</c> so
/// its <c>TryAddScoped&lt;IAuthUIService, AuthUIService&gt;()</c> defers to this stub.
/// <para>
/// A few canned addresses reach the sign-in and register states the E2E tier scans:
/// <see cref="TwoFactorEmail"/> signs in only with a code (the code step; <see cref="WrongCode"/> is
/// refused as invalid), <see cref="UnconfirmedEmail"/> is refused as unconfirmed (the resend prompt),
/// and registering <see cref="PendingEmail"/> answers "confirmation pending" (the check-your-email state).
/// </para>
/// </summary>
internal sealed class NoOpAuthUIService : IAuthUIService
{
    // Built from components rather than parsed: MA0176 rejects parsing a constant at runtime.
    private static readonly Guid CurrentDeviceSessionId =
        new(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11);

    private static readonly Guid OtherDeviceSessionId =
        new(0x22222222, 0x2222, 0x2222, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22);

    /// <summary>The address whose sign-in asks for a second-factor code.</summary>
    public const string TwoFactorEmail = "two-factor@gallery.test";

    /// <summary>The address whose sign-in is refused because it is unconfirmed.</summary>
    public const string UnconfirmedEmail = "unconfirmed@gallery.test";

    /// <summary>The address whose registration answers "confirmation pending".</summary>
    public const string PendingEmail = "pending@gallery.test";

    /// <summary>The code the canned two-factor sign-in refuses as invalid.</summary>
    public const string WrongCode = "000000";

    private static readonly Error Unavailable =
        Error.Failure("Gallery.AuthUnavailable", "The gallery has no backend.");

    public Task<Result<AuthenticationResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(CannedLogin(request));

    public Task<Result<AuthenticationResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAddress(request.Email, PendingEmail)
            ? Result.Success(RegistrationOutcome.ConfirmationPending)
            : Result.Failure<AuthenticationResponse>(Unavailable));

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

    // Canned history so the axe scan reaches the populated sign-in table: one live sign-in (the
    // current one) and one that ended, so both the "active" and the ended-reason cells render.
    public Task<Result<IReadOnlyList<SignInSummaryResponse>>> GetSignInsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SignInSummaryResponse> signIns =
        [
            new(
                SignInId: CurrentDeviceSessionId,
                StartedAt: new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
                LastActiveAt: new DateTime(2026, 1, 3, 14, 15, 0, DateTimeKind.Utc),
                IpAddress: "203.0.113.7",
                UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                EndedAt: null,
                EndReason: null,
                IsCurrent: true),
            new(
                SignInId: OtherDeviceSessionId,
                StartedAt: new DateTime(2025, 12, 20, 18, 30, 0, DateTimeKind.Utc),
                LastActiveAt: new DateTime(2025, 12, 21, 8, 0, 0, DateTimeKind.Utc),
                IpAddress: null,
                UserAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Mobile/15E148 Safari/604.1",
                EndedAt: new DateTime(2025, 12, 21, 8, 5, 0, DateTimeKind.Utc),
                EndReason: SignInEndReasons.SignedOut,
                IsCurrent: false),
        ];

        return Task.FromResult(Result.Success(signIns));
    }

    public Task<Result> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable));

    public Task<Result> RevokeAllSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable));

    private static bool IsAddress(string? email, string canned) =>
        string.Equals(email?.Trim(), canned, StringComparison.OrdinalIgnoreCase);

    // The second-factor answers mirror the API's: no code -> TwoFactorRequired; a wrong code ->
    // TwoFactorInvalid. Any other code still ends at "no backend", since the gallery opens no session.
    private static Result<AuthenticationResponse> CannedLogin(LoginRequest request)
    {
        if (IsAddress(request.Email, UnconfirmedEmail))
        {
            return Result.Failure<AuthenticationResponse>(Error.Unauthorized(
                AuthErrorCodes.EmailNotConfirmed,
                "Confirm your email address before signing in."));
        }

        if (!IsAddress(request.Email, TwoFactorEmail))
        {
            return Result.Failure<AuthenticationResponse>(Unavailable);
        }

        if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
        {
            return Result.Failure<AuthenticationResponse>(Error.Unauthorized(
                AuthErrorCodes.TwoFactorRequired,
                "A two-factor code is required."));
        }

        return string.Equals(request.TwoFactorCode, WrongCode, StringComparison.Ordinal)
            ? Result.Failure<AuthenticationResponse>(Error.Unauthorized(
                AuthErrorCodes.TwoFactorInvalid,
                "The two-factor code is invalid."))
            : Result.Failure<AuthenticationResponse>(Unavailable);
    }
}
