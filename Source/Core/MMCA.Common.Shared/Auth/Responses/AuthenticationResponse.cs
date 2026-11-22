namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// Response returned after successful authentication, containing JWT tokens and expiry metadata.
/// Shared between the Identity API and UI clients.
/// </summary>
/// <param name="AccessToken">The JWT access token for API authorization.</param>
/// <param name="RefreshToken">The opaque refresh token used to obtain new access tokens.</param>
/// <param name="AccessTokenExpiry">The UTC expiration time of the access token.</param>
public readonly record struct AuthenticationResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiry);

/// <summary>
/// The <c>202 Accepted</c> body of <c>auth/register</c> when the account was created but no session
/// was opened, because the host requires a confirmed address before sign-in. The client tells the
/// user to open the confirmation link, then sign in.
/// </summary>
/// <param name="Email">The address the account was registered with, as typed (trimmed).</param>
public sealed record RegistrationPendingResponse(string Email);

/// <summary>
/// The in-process signal for "registered, confirmation pending, no session": a successful
/// registration result that carries no tokens. Lets <c>IAuthenticationService.RegisterAsync</c> keep
/// its return type while telling the register endpoint to answer <c>202</c> instead of <c>201</c>.
/// </summary>
public static class RegistrationOutcome
{
    /// <summary>
    /// Gets the registration result value that means the account exists, the confirmation is pending,
    /// and no session was opened (empty tokens, <see cref="DateTime.MinValue"/> expiry).
    /// </summary>
    public static AuthenticationResponse ConfirmationPending { get; } =
        new(string.Empty, string.Empty, DateTime.MinValue);

    /// <summary>
    /// Whether <paramref name="response"/> is a registration that opened no session because the
    /// address still has to be confirmed. True for <see cref="ConfirmationPending"/> and for any value
    /// without an access token (including <see langword="default"/>).
    /// </summary>
    /// <param name="response">The registration result value.</param>
    /// <returns><see langword="true"/> when the response carries no access token.</returns>
    public static bool IsConfirmationPending(this AuthenticationResponse response) =>
        string.IsNullOrEmpty(response.AccessToken);
}
