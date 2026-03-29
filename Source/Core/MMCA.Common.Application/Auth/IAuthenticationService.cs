using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Defines the authentication workflows for the Identity module: login, registration,
/// token refresh, token revocation, and password change.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Authenticates a user with email and password credentials.
    /// </summary>
    /// <param name="request">Login credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the authentication tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new user account and returns authentication tokens.
    /// </summary>
    /// <param name="request">Registration details.</param>
    /// <param name="ipAddress">Optional client IP address for rate limiting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the authentication tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> RegisterAsync(
        RegisterRequest request,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges an expired access token and valid refresh token for a new token pair.
    /// </summary>
    /// <param name="request">The expired access token and current refresh token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the rotated tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> RefreshTokenAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the refresh token for the specified user.
    /// </summary>
    /// <param name="userId">The user whose token should be revoked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or a not-found error.</returns>
    Task<Result> RevokeTokenAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the password for the specified user.
    /// </summary>
    /// <param name="userId">The user changing their password.</param>
    /// <param name="request">Current and new password values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or an error.</returns>
    Task<Result> ChangePasswordAsync(
        UserIdentifierType userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a user via an external OAuth provider. Finds an existing account by
    /// provider+key or creates a new one from the OAuth claims.
    /// </summary>
    /// <param name="loginProvider">The OAuth provider name (e.g., "Google", "GitHub").</param>
    /// <param name="providerKey">The provider-specific unique identifier.</param>
    /// <param name="email">Email from OAuth claims.</param>
    /// <param name="firstName">First name from OAuth claims.</param>
    /// <param name="lastName">Last name from OAuth claims.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the authentication tokens, or an error.</returns>
    Task<Result<AuthenticationResponse>> ExternalLoginAsync(
        string loginProvider,
        string providerKey,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<AuthenticationResponse>(
            Error.Failure("Auth.ExternalLoginNotSupported", "External login is not supported.")));
}
