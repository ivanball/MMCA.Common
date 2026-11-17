namespace MMCA.Common.UI.Services.Auth.Devices;

/// <summary>
/// Reports the device this client runs on to the server, which records it on the caller's current
/// refresh session (<c>PUT auth/sessions/current/device</c>), so the signed-in devices list and the
/// sign-in history name the device instead of describing its user-agent.
/// <para>
/// <see cref="AuthUIService"/> calls it after every sign-in. <see cref="Tokens.SecureStoreTokenStorageService"/>
/// (the native heads' token storage) also calls it after a successful token refresh until one report
/// in the process has succeeded, so a session that began before the server stored device metadata is
/// described within one access-token lifetime, without a new sign-in. Rotation carries the recorded
/// metadata to every successor session, so nothing reports after that.
/// </para>
/// <para>
/// Best-effort by contract: an implementation should not throw, and the callers bound the wait and
/// swallow whatever escapes, so a slow or failing report never fails or holds up a sign-in or a
/// refresh. Web heads keep the no-op default (<see cref="NullDeviceMetadataReporter"/>); the MAUI
/// package registers the native implementation. Register an implementation as a singleton: the
/// once-per-process refresh report is tracked per reporter instance.
/// </para>
/// </summary>
public interface IDeviceMetadataReporter
{
    /// <summary>
    /// Reports this device's metadata for the session the stored access token belongs to.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token; the caller cancels it after a short bound.</param>
    /// <returns>
    /// <see langword="true"/> when the server recorded the report, or when there is nothing to report;
    /// <see langword="false"/> when it was not recorded, so the refresh backfill tries again on the
    /// next refresh.
    /// </returns>
    Task<bool> ReportAsync(CancellationToken cancellationToken = default);
}
