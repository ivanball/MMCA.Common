using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace MMCA.Common.UI.Layout;

/// <summary>
/// <see cref="FocusOnNavigate"/> for in-app navigation only: rendered from the first location change
/// on, so its first render (which always focuses) is the first navigation, never the initial load.
/// A location change that keeps the path and rewrites only the query string or fragment (a list page
/// mirroring its grid state into the URL) is not a navigation to another page and is ignored.
/// </summary>
public sealed partial class FocusOnInAppNavigate : ComponentBase, IDisposable
{
    private bool _hasNavigated;
    private string _initialPath = string.Empty;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <summary>Gets or sets the route data of the page being shown.</summary>
    [Parameter]
    [EditorRequired]
    public RouteData RouteData { get; set; } = default!;

    /// <summary>Gets or sets the CSS selector of the element to focus after a navigation; <c>h1</c> when unset.</summary>
    [Parameter]
    public string? Selector { get; set; }

    /// <inheritdoc />
    public void Dispose() => Navigation.LocationChanged -= OnLocationChanged;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _initialPath = Navigation.ToAbsoluteUri(Navigation.Uri).AbsolutePath;
        Navigation.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_hasNavigated
            || string.Equals(Navigation.ToAbsoluteUri(e.Location).AbsolutePath, _initialPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _hasNavigated = true;
        _ = InvokeAsync(StateHasChanged);
    }
}
