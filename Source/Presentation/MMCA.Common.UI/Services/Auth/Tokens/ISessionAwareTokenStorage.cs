namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// Optional capability of an <see cref="ITokenStorageService"/>: reports that a session it held has
/// ended on the server. A session can end without this client signing out (revoked from another
/// device with "Sign out everywhere", or the account deleted); the next refresh then answers a
/// DEFINITIVE "no session" (<see cref="TokenAcquisition.NoSession"/>), and without this signal the
/// shell stayed signed in behind 401s. <see cref="JwtAuthenticationStateProvider"/> subscribes and
/// flips the auth state to anonymous. A transient refresh failure never raises it. A storage that
/// does not implement this behaves as before.
/// </summary>
public interface ISessionAwareTokenStorage : ITokenStorageService
{
    /// <summary>
    /// Raised once when a refresh answered a definitive "no session" for a session this storage held.
    /// The stored access token is already cleared when it fires.
    /// </summary>
    event EventHandler? SessionEnded;

    /// <summary>
    /// The API answered <c>401</c> to a request that carried <paramref name="rejectedAccessToken"/>
    /// (<see cref="AuthDelegatingHandler"/> reports it). When this storage still holds that token, it
    /// is discarded and ONE refresh is attempted, through the same single-flight a stale token takes:
    /// a definitive "no session" ends the session exactly as an expired token's refresh does (the
    /// stored tokens are cleared and <see cref="SessionEnded"/> fires), and an acquired token replaces
    /// the rejected one. A token the storage no longer holds (already replaced, or never held by this
    /// instance) changes nothing.
    /// </summary>
    /// <param name="rejectedAccessToken">The access token the API refused.</param>
    /// <returns>The access token this storage holds afterwards, or <see langword="null"/> when none.</returns>
    /// <remarks>
    /// Without this, a session ended only when a refresh was refused, and an API <c>401</c> never
    /// caused one: a locked user's still-fresh token kept the shell signed in behind
    /// "you are not signed in" errors until it expired. The default implementation changes nothing,
    /// so a storage written before this member keeps compiling and behaves as before.
    /// </remarks>
    Task<string?> RenewRejectedAccessTokenAsync(string rejectedAccessToken) => Task.FromResult<string?>(null);
}
