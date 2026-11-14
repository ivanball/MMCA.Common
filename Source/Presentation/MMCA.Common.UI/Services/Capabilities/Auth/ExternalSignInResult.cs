namespace MMCA.Common.UI.Services.Capabilities.Auth;

/// <summary>
/// How a brokered external sign-in (<see cref="IExternalAuthBroker.SignInAsync"/>) ended: whether the
/// user ended up authenticated and, when the server refused the sign-in, the server's error code
/// (for example <c>Auth.ExternalEmailNotVerified</c>) so the Login page can show the same mapped
/// refusal the web flow shows, instead of a generic "try again" for a sign-in that can never succeed.
/// </summary>
public sealed class ExternalSignInResult
{
    private ExternalSignInResult(bool succeeded, string? errorCode)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the result of a sign-in that handed off to the completion page.</summary>
    public static ExternalSignInResult Success { get; } = new(succeeded: true, errorCode: null);

    /// <summary>Gets a value indicating whether the user ended up authenticated.</summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the server's error code when the server refused the sign-in, or <see langword="null"/> when
    /// it succeeded or ended without one (dismissed, cancelled, unsupported, an unreadable callback).
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>Creates the result of a sign-in that did not authenticate the user.</summary>
    /// <param name="errorCode">The server's error code, or <see langword="null"/> when there is none.</param>
    /// <returns>The failed result.</returns>
    public static ExternalSignInResult Failed(string? errorCode = null) => new(succeeded: false, errorCode);
}
