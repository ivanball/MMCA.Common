using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace MMCA.Common.UI.Layout;

/// <summary>
/// <see cref="FocusOnNavigate"/> for in-app navigation only: rendered from the first location change
/// on, so its first render (which always focuses) is the first navigation, never the initial load.
/// </summary>
public sealed partial class FocusOnInAppNavigate : ComponentBase, IDisposable
{
    private bool _hasNavigated;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <summary>Gets or sets the route data of the page being shown.</summary>
    [Parameter]
    [EditorRequired]
    public RouteData RouteData { get; set; } = default!;

    /// <summary>Gets or sets the CSS selector of the element to focus after a navigation.</summary>
    [Parameter]
    public string Selector { get; set; } = "h1";

    /// <inheritdoc />
    public void Dispose() => Navigation.LocationChanged -= OnLocationChanged;

    /// <inheritdoc />
    protected override void OnInitialized() => Navigation.LocationChanged += OnLocationChanged;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_hasNavigated)
        {
            return;
        }

        _hasNavigated = true;
        _ = InvokeAsync(StateHasChanged);
    }
}
