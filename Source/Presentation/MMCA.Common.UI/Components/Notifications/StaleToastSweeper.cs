using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace MMCA.Common.UI.Components.Notifications;

/// <summary>
/// Removes toasts left over from the page being left, so a toast about that page ("Item added")
/// does not follow the user onto the next one. Rendered by <c>MainLayout</c> only when
/// <c>Layout:ClearToastsOnNavigation</c> is on; renders nothing itself.
/// </summary>
/// <remarks>
/// A toast raised in the same moment as the navigation (an action that reports its outcome and then
/// moves on, "Order created, taking you to it") is about the page being opened, so only toasts that
/// had been on screen for longer than <see cref="Grace"/> before the navigation are removed. The
/// first time each toast is seen is recorded from <see cref="ISnackbar.OnSnackbarsUpdated"/>.
/// </remarks>
public sealed class StaleToastSweeper : ComponentBase, IDisposable
{
    /// <summary>How long a toast must have been on screen before a navigation counts as leaving it behind.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(750);

    private readonly Dictionary<Snackbar, DateTimeOffset> _firstSeen = [];
    private readonly Lock _sync = new();

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private TimeProvider TimeProvider { get; set; } = default!;

    /// <inheritdoc />
    public void Dispose()
    {
        Navigation.LocationChanged -= OnLocationChanged;
        Snackbar.OnSnackbarsUpdated -= OnSnackbarsUpdated;
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Navigation.LocationChanged += OnLocationChanged;
        Snackbar.OnSnackbarsUpdated += OnSnackbarsUpdated;
        OnSnackbarsUpdated();
    }

    // Snackbar updates arrive on timer threads (expiry), hence the lock.
    private void OnSnackbarsUpdated()
    {
        var now = TimeProvider.GetUtcNow();
        var shown = Snackbar.ShownSnackbars.ToHashSet();

        lock (_sync)
        {
            foreach (var snackbar in shown)
            {
                _firstSeen.TryAdd(snackbar, now);
            }

            foreach (var gone in _firstSeen.Keys.Where(snackbar => !shown.Contains(snackbar)).ToList())
            {
                _firstSeen.Remove(gone);
            }
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var cutoff = TimeProvider.GetUtcNow() - Grace;
        List<Snackbar> stale;

        lock (_sync)
        {
            stale = [.. _firstSeen.Where(entry => entry.Value <= cutoff).Select(entry => entry.Key)];
            foreach (var snackbar in stale)
            {
                _firstSeen.Remove(snackbar);
            }
        }

        foreach (var snackbar in stale)
        {
            Snackbar.Remove(snackbar);
        }
    }
}
