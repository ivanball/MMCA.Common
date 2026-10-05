using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Renders nothing; on initialization it reports the current route as not found through
/// <see cref="NavigationManager.NotFound"/>. The router places it beside the not-found page it
/// renders for a hidden notification page (<see cref="NotificationPageGate"/>), so a server render
/// answers with status 404 rather than 200.
/// </summary>
internal sealed class NotificationPageNotFoundStatus : ComponentBase
{
    /// <summary>Gets or sets the navigation manager that carries the not-found signal.</summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnInitialized() => Navigation.NotFound();
}
