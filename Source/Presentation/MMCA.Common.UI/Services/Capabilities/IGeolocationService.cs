namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Soft, one-shot device location for proximity hints ("~3 km from the venue"). Never
/// blocks a feature: permission denial or unavailability yields <see langword="null"/>,
/// and callers simply omit the hint.
/// </summary>
public interface IGeolocationService
{
    /// <summary>Whether the platform can provide a location at all (web/null fallbacks report <see langword="false"/>).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Returns the last-known position when fresh enough, otherwise a single current-position
    /// read. Triggers the platform permission prompt at most once; returns <see langword="null"/>
    /// on denial, timeout, or any platform failure.
    /// </summary>
    Task<GeoPoint?> GetCurrentOrLastKnownAsync(CancellationToken cancellationToken = default);
}
