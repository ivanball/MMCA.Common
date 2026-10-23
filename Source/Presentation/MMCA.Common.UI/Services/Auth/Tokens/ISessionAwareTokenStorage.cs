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
}
