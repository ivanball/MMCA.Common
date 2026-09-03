using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Last-known-good snapshot of a list page's FIRST page (ADR-042), so a dead network still shows
/// content. Purely best-effort, and only ever consulted for page 1 while the device reports itself
/// offline, so the live path is never affected: a page wires it into its server-data delegate,
/// calling <see cref="RememberAsync"/> on every success and <see cref="TryReadAsync"/> when a fetch
/// fails. Backed by <see cref="ILocalCacheStore"/>, which persists on the MAUI and WebAssembly heads
/// and reports itself unavailable on Blazor Server (SSR always has the live API).
/// </summary>
/// <typeparam name="TItem">The list item DTO type; must be JSON round-trippable.</typeparam>
/// <param name="store">The device-local cache store; unavailable on heads that have none.</param>
/// <param name="connectivity">Reports whether the device currently has a network.</param>
/// <param name="cacheKey">
/// Cache key for this page's snapshot. Must be unique per list surface (and per scope, when one
/// head shows the same list for different tenants or events), since a shared key would let one page
/// serve another page's rows.
/// </param>
public sealed class OfflineFirstPageSnapshot<TItem>(
    ILocalCacheStore store,
    IConnectivityStatusService connectivity,
    string cacheKey)
{
    private sealed record CachedPage(List<TItem> Items, int TotalItems);

    /// <summary>True when a failed first-page fetch may be answered from the snapshot.</summary>
    /// <param name="page">The 1-based page the grid asked for.</param>
    public bool CanServe(int page) => !connectivity.IsOnline && store.IsAvailable && page == 1;

    /// <summary>Records a freshly fetched first page; any other page is left alone.</summary>
    /// <param name="fetched">The page the server returned.</param>
    /// <param name="page">The 1-based page the grid asked for.</param>
    /// <param name="cancellationToken">The fetch's cancellation token.</param>
    public async Task RememberAsync(
        (IReadOnlyList<TItem> Items, int TotalItems) fetched,
        int page,
        CancellationToken cancellationToken = default)
    {
        if (page == 1 && store.IsAvailable)
        {
            await store.SetAsync(
                cacheKey, new CachedPage([.. fetched.Items], fetched.TotalItems), cancellationToken);
        }
    }

    /// <summary>
    /// Reads the snapshot, or <see langword="null"/> when the device is online, the store is
    /// unavailable, this is not the first page, or nothing was cached.
    /// </summary>
    /// <param name="page">The 1-based page the grid asked for.</param>
    /// <param name="cancellationToken">The fetch's cancellation token.</param>
    public async Task<(IReadOnlyList<TItem> Items, int TotalItems)?> TryReadAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        if (!CanServe(page))
        {
            return null;
        }

        var cached = await store.GetAsync<CachedPage>(cacheKey, cancellationToken);
        return cached is null ? null : (cached.Items, cached.TotalItems);
    }
}
