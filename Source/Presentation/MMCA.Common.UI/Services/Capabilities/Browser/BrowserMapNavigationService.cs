namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="IMapNavigationService"/>: opens a Google Maps search for the address
/// in a new tab via <see cref="IExternalLinkService"/> (no native maps app on the web).
/// </summary>
public sealed class BrowserMapNavigationService : IMapNavigationService
{
    // The public Google Maps search endpoint IS the integration point on the web — there is
    // no service to discover or configure (S1075 targets environment-dependent paths).
#pragma warning disable S1075
    private const string MapsSearchUrl = "https://www.google.com/maps/search/?api=1&query=";
#pragma warning restore S1075

    private readonly IExternalLinkService _externalLinkService;

    /// <summary>Initializes the service over the host's external-link opener.</summary>
    public BrowserMapNavigationService(IExternalLinkService externalLinkService) =>
        _externalLinkService = externalLinkService;

    /// <inheritdoc />
    public async Task<bool> OpenAddressAsync(string address, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var uri = new Uri(MapsSearchUrl + Uri.EscapeDataString(address));
        await _externalLinkService.OpenAsync(uri, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
