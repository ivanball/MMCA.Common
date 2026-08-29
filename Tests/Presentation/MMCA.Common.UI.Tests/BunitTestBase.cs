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
/// Kept as a thin repo-local extension point so Common-only service registrations can be added in one place.
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

        // CultureSwitcher (same top-row) injects ICultureApplier, and Login injects it to apply the
        // signed-in user's stored culture. The production web default, so layout tests exercise the
        // real navigation; a test that cares about the call itself substitutes its own.
        Services.AddScoped<ICultureApplier, EndpointCultureApplier>();

        // Capability defaults the shared pages/layout inject (ADR-042): Login consults the
        // external-auth broker, MainLayout renders the OfflineBanner. Registered here rather than in
        // the Testing.UI harness because the head owns which capability implementations win, and a
        // consumer's MAUI or browser head substitutes its own.
        Services.AddSingleton<IExternalAuthBroker, UnavailableExternalAuthBroker>();
        Services.AddSingleton<IConnectivityStatusService, AlwaysOnlineConnectivityStatusService>();

        // Shareable public links (share/copy-link/QR components). The browser-origin default, which
        // resolves against bUnit's http://localhost/ base uri; a test that cares about the MAUI
        // public-site behaviour substitutes its own builder.
        Services.AddScoped<IPublicLinkBuilder, NavigationPublicLinkBuilder>();

        // The toast and confirm-dialog facades come from the shared harness (AddCommonUiFacades,
        // called by BunitComponentTestBase over the MudBlazor services it registers), so a component
        // test exercises the real Mud-backed path with no registration here. A test that asserts on
        // toasts (or answers a confirm) registers its own double: last registration wins.
    }
}
