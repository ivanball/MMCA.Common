namespace MMCA.Common.UI.Services.Capabilities.Location;

/// <summary>Default <see cref="IMapNavigationService"/>: no maps integration; reports failure.</summary>
public sealed class NullMapNavigationService : IMapNavigationService
{
    /// <inheritdoc />
    public Task<bool> OpenAddressAsync(string address, string? label, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
