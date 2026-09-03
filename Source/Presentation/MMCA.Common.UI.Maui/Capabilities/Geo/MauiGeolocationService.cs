using MMCA.Common.UI.Services.Capabilities.Geo;

namespace MMCA.Common.UI.Maui.Capabilities.Geo;

/// <summary>
/// MAUI <see cref="IGeolocationService"/> over <c>Geolocation.Default</c> with a soft
/// when-in-use permission flow: the platform prompt appears at most once per install, and
/// denial, disabled location services, timeouts, and platform errors all yield
/// <see langword="null"/> — callers omit the proximity hint, nothing breaks.
/// </summary>
public sealed class MauiGeolocationService : IGeolocationService
{
    private static readonly TimeSpan LastKnownFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CurrentFixTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<GeoPoint?> GetCurrentOrLastKnownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>().ConfigureAwait(false);
#if ANDROID
            // Android 12+ "Approximate only" grants coarse and denies fine, and the composite
            // LocationWhenInUse check reports that as Denied. Probe coarse alone so an existing
            // approximate grant is honored without re-showing the precise-upgrade prompt on
            // every read.
            if (status != PermissionStatus.Granted
                && await Permissions.CheckStatusAsync<CoarseLocationOnly>().ConfigureAwait(false) == PermissionStatus.Granted)
            {
                status = PermissionStatus.Restricted;
            }
#endif
            if (status is not (PermissionStatus.Granted or PermissionStatus.Restricted))
            {
                status = await MainThread.InvokeOnMainThreadAsync(
                    static () => Permissions.RequestAsync<Permissions.LocationWhenInUse>()).ConfigureAwait(false);
            }

            // Restricted is a partial grant (Android approximate-only: RequestAsync counts one of
            // the two runtime permissions granted). Essentials' own Geolocation calls accept
            // Granted-or-Restricted; rejecting Restricted here would turn the deliberate coarse
            // design into a silent no-hint for every approximate user.
            if (status is not (PermissionStatus.Granted or PermissionStatus.Restricted))
            {
                return null;
            }

            var lastKnown = await Geolocation.Default.GetLastKnownLocationAsync().ConfigureAwait(false);
            if (lastKnown is not null && IsFresh(lastKnown))
            {
                return new GeoPoint(lastKnown.Latitude, lastKnown.Longitude);
            }

            var request = new GeolocationRequest(GeolocationAccuracy.Medium, CurrentFixTimeout);
            var current = await Geolocation.Default.GetLocationAsync(request, cancellationToken).ConfigureAwait(false);
            return current is null ? null : new GeoPoint(current.Latitude, current.Longitude);
        }
        catch (FeatureNotSupportedException)
        {
            return null;
        }
        catch (FeatureNotEnabledException)
        {
            // Location services switched off at the OS level.
            return null;
        }
        catch (PermissionException)
        {
            return null;
        }
    }

    private static bool IsFresh(Location location) =>
        location.Timestamp >= DateTimeOffset.UtcNow - LastKnownFreshness;

#if ANDROID
    /// <summary>
    /// Coarse-only status probe. MAUI's <c>LocationWhenInUse</c> lists BOTH coarse and fine as
    /// required, so its <c>CheckStatusAsync</c> can never say Granted for an approximate-only
    /// grant; this subclass asks about coarse alone.
    /// </summary>
    private sealed class CoarseLocationOnly : Permissions.BasePlatformPermission
    {
        public override (string, bool)[] RequiredPermissions =>
            [(global::Android.Manifest.Permission.AccessCoarseLocation, true)];
    }
#endif
}
