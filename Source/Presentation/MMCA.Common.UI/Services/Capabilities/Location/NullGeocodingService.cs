namespace MMCA.Common.UI.Services.Capabilities.Location;

/// <summary>Default <see cref="IGeocodingService"/>: no geocoder; always <see langword="null"/>.</summary>
public sealed class NullGeocodingService : IGeocodingService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<GeoPoint?> GeocodeAsync(string address, CancellationToken cancellationToken = default) =>
        Task.FromResult<GeoPoint?>(null);
}
