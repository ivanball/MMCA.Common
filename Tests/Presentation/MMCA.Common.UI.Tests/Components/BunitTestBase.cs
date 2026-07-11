using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Fallbacks;

namespace MMCA.Common.UI.Tests;

/// <summary>
/// Repo-local base for MMCA.Common UI component tests. Inherits the shared
/// <see cref="BunitComponentTestBase"/> (MudBlazor services, loose JSInterop, auth test doubles,
/// and the MudBlazor provider + interaction helpers) from the <c>MMCA.Common.Testing.UI</c> package.
/// Kept as a thin repo-local seam so Common-only service registrations can be added in one place.
/// </summary>
public abstract class BunitTestBase : BunitComponentTestBase
{
    protected BunitTestBase()
    {
        // NavMenu's mobile top-row renders ThemeToggle/CultureSwitcher unconditionally (the ADR-027/028
        // mobile-parity fix), and ThemeToggle injects the JS-backed ThemeService; bUnit's loose
        // JSInterop satisfies its calls. Registered here (not in the shared harness) because only
        // Common's own tests render the layout chrome; consumer bUnit tests render pages directly.
        Services.AddScoped<ThemeService>();

        // Capability defaults the shared pages/layout inject (ADR-042): Login consults the
        // external-auth broker, MainLayout renders the OfflineBanner. The Testing.UI harness
        // cannot register these (it deliberately does not reference MMCA.Common.UI).
        Services.AddSingleton<IExternalAuthBroker, UnavailableExternalAuthBroker>();
        Services.AddSingleton<IConnectivityStatusService, AlwaysOnlineConnectivityStatusService>();
    }
}
