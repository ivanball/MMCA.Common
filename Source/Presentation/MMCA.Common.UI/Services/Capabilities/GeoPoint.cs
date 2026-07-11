namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// A latitude/longitude pair returned by <see cref="IGeolocationService"/>.
/// Kept transport-agnostic so shared components never touch platform location types.
/// </summary>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
public sealed record GeoPoint(double Latitude, double Longitude)
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Great-circle distance to <paramref name="other"/> in kilometers (haversine formula).
    /// Precise enough for "how far is the venue" hints; not for navigation.
    /// </summary>
    public double DistanceKmTo(GeoPoint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var dLat = ToRadians(other.Latitude - Latitude);
        var dLon = ToRadians(other.Longitude - Longitude);
        var sinLat = Math.Sin(dLat / 2);
        var sinLon = Math.Sin(dLon / 2);
        var cosines = Math.Cos(ToRadians(Latitude)) * Math.Cos(ToRadians(other.Latitude));
        var a = sinLat * sinLat + cosines * sinLon * sinLon;
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
