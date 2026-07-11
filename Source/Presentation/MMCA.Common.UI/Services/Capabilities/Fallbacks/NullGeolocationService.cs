namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IGeolocationService"/>: no location source; always <see langword="null"/>.</summary>
public sealed class NullGeolocationService : IGeolocationService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<GeoPoint?> GetCurrentOrLastKnownAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<GeoPoint?>(null);
}
