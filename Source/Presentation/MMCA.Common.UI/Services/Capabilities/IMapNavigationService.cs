namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Opens the platform maps experience for a street address (MAUI launches the native maps
/// app; browsers open a maps website in a new tab). Address-only by design — the domain
/// model carries no geo-coordinates.
/// </summary>
public interface IMapNavigationService
{
    /// <summary>
    /// Opens maps pointed at <paramref name="address"/>, labeled <paramref name="label"/>
    /// where the platform supports it. Returns whether a maps UI was opened.
    /// </summary>
    Task<bool> OpenAddressAsync(string address, string? label, CancellationToken cancellationToken = default);
}
