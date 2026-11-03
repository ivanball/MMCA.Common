// The MAUI TFMs import this namespace implicitly (IDE0005 would flag it); the net10.0 test project
// that compiles this file as linked source does not.
#if !(ANDROID || IOS || MACCATALYST || WINDOWS)
using Microsoft.Extensions.DependencyInjection;
#endif
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities.Interop;

/// <summary>
/// Registers the shared <see cref="CapabilitiesJsModule"/> on a MAUI Blazor Hybrid head. The
/// sign-in-link, confirm-email and reset-password pages read their credential out of the URL fragment
/// through it (<c>locationHashTake</c>), and resolve it optionally, so a head that never registered it
/// silently read a null fragment and showed "This link is incomplete.". BlazorWebView serves the RCL
/// static assets under <c>_content/</c> like a browser host does (the back-navigation bridge already
/// imports <c>nav-interop.js</c> there), so the same module works unchanged.
/// <para>
/// Kept free of MAUI APIs so the framework's net10.0 test project can compile it as linked source.
/// TryAdd, so a hybrid head that also calls <c>AddBrowserDeviceCapabilities</c> keeps one registration.
/// </para>
/// </summary>
internal static class MauiCapabilitiesJsModuleRegistration
{
    /// <summary>Registers the scoped <see cref="CapabilitiesJsModule"/> unless one is already registered.</summary>
    /// <param name="services">The head's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddMauiCapabilitiesJsModule(this IServiceCollection services)
    {
        services.TryAddScoped<CapabilitiesJsModule>();
        return services;
    }
}
