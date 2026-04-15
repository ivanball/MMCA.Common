using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Provides brute-force and rate-limiting protection for authentication workflows.
/// Implementations use a distributed or in-memory cache to track failed login attempts
/// per email and registration attempts per IP address.
/// </summary>
public interface ILoginProtectionService
{
    /// <summary>
    /// Checks whether the specified email is currently locked out due to excessive failed login attempts.
    /// </summary>
    /// <param name="email">The email to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="Result.Success()"/> if not locked out; a failure result with an appropriate error otherwise.</returns>
    Task<Result> CheckLockoutAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed login attempt for the specified email and applies exponential backoff lockout
    /// when the maximum number of failed attempts is exceeded.
    /// </summary>
    /// <param name="email">The email that failed authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IncrementFailedAttemptsAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the failed attempt counter and lockout for the specified email after a successful login.
    /// </summary>
    /// <param name="email">The email to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResetFailedAttemptsAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the specified IP address has exceeded the registration rate limit.
    /// Returns <see cref="Result.Success()"/> when no IP is provided or the limit is not reached.
    /// </summary>
    /// <param name="ipAddress">The client IP address, or <see langword="null"/> to skip the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="Result.Success()"/> if within limit; a failure result otherwise.</returns>
    Task<Result> CheckRegistrationRateLimitAsync(string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the registration count for the specified IP address.
    /// </summary>
    /// <param name="ipAddress">The client IP address, or <see langword="null"/> to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IncrementRegistrationCountAsync(string? ipAddress, CancellationToken cancellationToken = default);
}
