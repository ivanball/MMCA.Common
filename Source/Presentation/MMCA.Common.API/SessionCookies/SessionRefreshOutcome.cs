namespace MMCA.Common.API.SessionCookies;

/// <summary>How an attempt to obtain a usable access token from the session cookies ended.</summary>
public enum SessionRefreshStatus
{
    /// <summary>
    /// A usable access token is available: the access cookie was still valid, or the refresh cookie
    /// was exchanged for a rotated pair (written back as cookies).
    /// </summary>
    Refreshed = 0,

    /// <summary>
    /// The session is definitively over: there is no refresh cookie, or the identity endpoint refused
    /// the refresh token (400, 401 or 403). Clearing the cookies is correct.
    /// </summary>
    Rejected = 1,

    /// <summary>
    /// The refresh could not be decided right now (a 5xx, 429 or other non-refusal status, a timeout, a
    /// network failure or an unreadable body). The cookies may still hold a live session, so they must
    /// be kept and the call retried later.
    /// </summary>
    Unavailable = 2,
}

/// <summary>
/// The result of <see cref="ICookieSessionRefresher.ValidateOrRefreshAsync"/> and
/// <see cref="ICookieSessionRefresher.RefreshAsync"/>: the status, the token when it is
/// <see cref="SessionRefreshStatus.Refreshed"/>, and the upstream's <c>Retry-After</c> hint when it is
/// <see cref="SessionRefreshStatus.Unavailable"/>.
/// </summary>
public sealed class SessionRefreshOutcome
{
    private static readonly SessionRefreshOutcome RejectedOutcome = new(SessionRefreshStatus.Rejected, session: null, retryAfter: null);

    private SessionRefreshOutcome(SessionRefreshStatus status, SessionTokenResult? session, TimeSpan? retryAfter)
    {
        Status = status;
        Session = session;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets how the attempt ended.</summary>
    public SessionRefreshStatus Status { get; }

    /// <summary>Gets the usable access token; set only when <see cref="Status"/> is <see cref="SessionRefreshStatus.Refreshed"/>.</summary>
    public SessionTokenResult? Session { get; }

    /// <summary>
    /// Gets the delay the identity endpoint asked for (its <c>Retry-After</c>), when
    /// <see cref="Status"/> is <see cref="SessionRefreshStatus.Unavailable"/> and it sent one.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>A usable access token.</summary>
    /// <param name="session">The access token and its expiry.</param>
    /// <returns>A <see cref="SessionRefreshStatus.Refreshed"/> outcome.</returns>
    public static SessionRefreshOutcome Refreshed(SessionTokenResult session) =>
        new(SessionRefreshStatus.Refreshed, session, retryAfter: null);

    /// <summary>The session is definitively over.</summary>
    /// <returns>A <see cref="SessionRefreshStatus.Rejected"/> outcome.</returns>
    public static SessionRefreshOutcome Rejected() => RejectedOutcome;

    /// <summary>The refresh could not be decided right now.</summary>
    /// <param name="retryAfter">The upstream's <c>Retry-After</c> delay, if it sent one.</param>
    /// <returns>An <see cref="SessionRefreshStatus.Unavailable"/> outcome.</returns>
    public static SessionRefreshOutcome Unavailable(TimeSpan? retryAfter = null) =>
        new(SessionRefreshStatus.Unavailable, session: null, retryAfter);
}
