namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Runs an external OAuth sign-in (Google/GitHub) through the platform's system-browser
/// authenticator instead of the web redirect flow — embedded WebViews are rejected by the
/// providers. The default broker is unavailable, which preserves the existing anchor-href
/// flow on web heads; the MAUI implementation drives <c>WebAuthenticator</c> against the
/// API's OAuth endpoints and stores the resulting token pair.
/// </summary>
public interface IExternalAuthBroker
{
    /// <summary>Whether a native brokered sign-in is available on this host.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs the full brokered flow for <paramref name="provider"/> (e.g. <c>google</c>,
    /// <c>github</c>): system-browser challenge, callback capture, code exchange, token
    /// storage. Returns whether the user ended up authenticated.
    /// </summary>
    Task<bool> SignInAsync(string provider, CancellationToken cancellationToken = default);
}
