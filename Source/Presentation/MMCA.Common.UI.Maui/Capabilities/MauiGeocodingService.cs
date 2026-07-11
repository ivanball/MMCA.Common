using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IGeocodingService"/> over <c>Geocoding.Default</c>. No permissions apply
/// (geocoding is a network lookup, not a location read); failures degrade to
/// <see langword="null"/> like every proximity affordance.
/// </summary>
public sealed class MauiGeocodingService : IGeocodingService
{
    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<GeoPoint?> GeocodeAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        try
        {
            var locations = await Geocoding.Default.GetLocationsAsync(address).ConfigureAwait(false);
            var first = locations?.FirstOrDefault();
            return first is null ? null : new GeoPoint(first.Latitude, first.Longitude);
        }
        catch (FeatureNotSupportedException)
        {
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            // Geocoder unavailable/offline — the proximity hint is simply omitted.
            return null;
        }
    }
}
