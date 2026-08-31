using System.Reflection;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Minimal <see cref="IUIModule"/> whose <see cref="Assembly"/> is the gallery itself, so the shared
/// Router (<c>Routes.razor</c>, which scans <c>UIModules.Select(m =&gt; m.Assembly)</c>) discovers the
/// gallery's <c>/components</c> page. The nav links make the host browsable when run interactively.
/// </summary>
internal sealed class GalleryUIModule : IUIModule
{
    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new("Login", "/login", Icons.Material.Filled.Login, typeof(SharedResource)),
        new("Register", "/register", Icons.Material.Filled.PersonAdd, typeof(SharedResource)),
        new("Components", "/components", Icons.Material.Filled.Widgets, typeof(SharedResource)),
        new("Grid", "/grid", Icons.Material.Filled.TableRows, typeof(SharedResource)),
    ];

    public Assembly Assembly => typeof(GalleryUIModule).Assembly;
}
