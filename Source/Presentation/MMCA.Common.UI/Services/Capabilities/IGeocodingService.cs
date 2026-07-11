namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Resolves a street address to coordinates for proximity hints ("~3 km from the venue").
/// Best-effort by contract: unsupported hosts and failed lookups return <see langword="null"/>
/// and callers omit the hint. The domain model deliberately carries no coordinates (addresses
/// only), so this is the only place they ever exist.
/// </summary>
public interface IGeocodingService
{
    /// <summary>Whether this platform can geocode at all (web/null fallbacks report <see langword="false"/>).</summary>
    bool IsSupported { get; }

    /// <summary>Returns the first coordinate match for <paramref name="address"/>, or <see langword="null"/>.</summary>
    Task<GeoPoint?> GeocodeAsync(string address, CancellationToken cancellationToken = default);
}
