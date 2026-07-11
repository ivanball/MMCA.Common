using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

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
            if (status != PermissionStatus.Granted)
            {
                status = await MainThread.InvokeOnMainThreadAsync(
                    static () => Permissions.RequestAsync<Permissions.LocationWhenInUse>()).ConfigureAwait(false);
            }

            if (status != PermissionStatus.Granted)
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
}
