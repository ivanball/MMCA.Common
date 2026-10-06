namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// The outcome of one access-token acquisition, telling a definitive "there is no session" apart
/// from an attempt that failed for a reason that says nothing about the session (a 429 or 5xx from
/// the token endpoint, a dropped connection, JS interop unavailable, a cancelled call). Token storage
/// remembers only the first kind, so a transient failure is retried on the very next call.
/// </summary>
public sealed class TokenAcquisition
{
    private TokenAcquisition(string? accessToken, bool isUnavailable)
    {
        AccessToken = accessToken;
        IsUnavailable = isUnavailable;
    }

    /// <summary>Gets the outcome for an answer that proves there is no session (the endpoint's 401).</summary>
    public static TokenAcquisition NoSession { get; } = new(null, isUnavailable: false);

    /// <summary>Gets the outcome for an attempt that failed without saying anything about the session.</summary>
    public static TokenAcquisition Unavailable { get; } = new(null, isUnavailable: true);

    /// <summary>Gets the acquired access token, or <see langword="null"/> when none was acquired.</summary>
    public string? AccessToken { get; }

    /// <summary>
    /// Gets a value indicating whether the attempt failed transiently, so the absence of a token
    /// proves nothing about the session and the next read should try again.
    /// </summary>
    public bool IsUnavailable { get; }

    /// <summary>Creates the outcome for an acquired token.</summary>
    /// <param name="accessToken">The acquired access token.</param>
    /// <returns>The outcome.</returns>
    public static TokenAcquisition Acquired(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return new(accessToken, isUnavailable: false);
    }
}
