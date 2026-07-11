using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IMapNavigationService"/>: launches the platform maps app with an
/// address query via <c>Launcher</c> URIs (<c>geo:</c> on Android, Apple Maps on iOS/Mac,
/// Bing Maps on Windows). Address-only by design — no geocoding, no location permission.
/// Android hosts need a <c>geo</c> intent entry in the manifest <c>&lt;queries&gt;</c> block.
/// </summary>
public sealed class MauiMapNavigationService : IMapNavigationService
{
    /// <inheritdoc />
    public async Task<bool> OpenAddressAsync(string address, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var query = Uri.EscapeDataString(address);
        var uri = BuildPlatformUri(query);

        try
        {
            return await Launcher.Default.TryOpenAsync(uri).ConfigureAwait(false);
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
    }

    private static Uri BuildPlatformUri(string escapedQuery)
    {
        // These launcher URIs ARE the per-platform maps integration point — fixed by the OS,
        // not environment-dependent (S1075 targets configurable paths).
#pragma warning disable S1075
        if (OperatingSystem.IsAndroid())
        {
            return new Uri("geo:0,0?q=" + escapedQuery);
        }

        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            return new Uri("https://maps.apple.com/?q=" + escapedQuery);
        }

        return new Uri("bingmaps:?q=" + escapedQuery);
#pragma warning restore S1075
    }
}
