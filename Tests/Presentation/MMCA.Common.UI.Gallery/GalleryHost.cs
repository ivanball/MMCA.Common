using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Gallery.Components;
using MMCA.Common.UI.Gallery.Stubs;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Notifications;
using MudBlazor.Services;

namespace MMCA.Common.UI.Gallery;

/// <summary>
/// Builds the backend-less Blazor gallery host. It renders the real <c>MMCA.Common.UI</c> auth pages
/// (<c>/login</c>, <c>/register</c>) and a primitives showcase (<c>/components</c>) with stub
/// implementations of the consumer seams (no-op auth, anonymous auth state, null token storage), so a
/// real-browser axe accessibility scan can run against the shared UI inside <c>MMCA.Common</c>'s own CI.
/// </summary>
public static class GalleryHost
{
    /// <summary>
    /// Builds (but does not start) the configured gallery <see cref="WebApplication"/>. Callers either
    /// <c>RunAsync()</c> it (the <c>dotnet run</c> entry point) or <c>StartAsync()</c> it on an ephemeral
    /// port from the E2E collection fixture.
    /// </summary>
    public static WebApplication BuildApp(string[] args)
    {
        // No null-forgiving operator here: CI's nullable analysis treats AssemblyName.Name as non-null
        // and flags `!` as an unnecessary suppression (IDE0370). The value is only ever interpolated
        // into a manifest filename below, which is null-safe regardless.
        var galleryAssemblyName = typeof(GalleryHost).Assembly.GetName().Name;
        var baseDir = AppContext.BaseDirectory;

        var builder = WebApplication.CreateBuilder(args);

        // Static web assets (the RCL _content/* CSS+JS and _framework/blazor.web.js) are resolved from
        // the *entry* assembly's manifests and are auto-loaded only in Development. When the E2E suite
        // self-hosts the gallery in-process the entry assembly is the test host and the environment is
        // Production, so neither default holds. Point the loader explicitly at the gallery's runtime
        // manifest (copied next to the gallery DLL) and force it on. Without this the auth pages render
        // unstyled and never become interactive, so axe's contrast checks are meaningless and the page
        // never signals Blazor readiness.
        builder.WebHost.UseSetting(
            WebHostDefaults.StaticWebAssetsKey,
            Path.Combine(baseDir, $"{galleryAssemblyName}.staticwebassets.runtime.json"));
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddMudServices();

        // Stub the consumer seams BEFORE AddUIShared so its TryAdd* registrations defer to these.
        // The gallery never performs real auth or token I/O. The auth pages render in their
        // signed-out state so axe scans the anonymous markup; the [Authorize]-guarded notification
        // pages (rubric §25) are scanned signed-in via the cookie-toggled fake scheme below.
        builder.Services.AddScoped<IAuthUIService, NoOpAuthUIService>();
        builder.Services.AddScoped<ITokenStorageService, NullTokenStorageService>();
        builder.Services.AddScoped<ITokenRefresher, NullTokenRefresher>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<AuthenticationStateProvider, GalleryAuthenticationStateProvider>();

        // The notification pages carry a real [Authorize], which MapRazorComponents surfaces as
        // endpoint metadata, so the host needs a genuine authentication scheme and the full
        // authorization stack. GalleryFakeAuthenticationHandler authenticates only requests carrying
        // the gallery_auth=1 cookie (the notification E2E scans); everything else stays anonymous.
        builder.Services
            .AddAuthentication(GalleryFakeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, GalleryFakeAuthenticationHandler>(
                GalleryFakeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        // Canned notification seams so NotificationBell and the notification pages
        // (/notifications, /notifications/inbox, /notifications/send — discovered from the
        // MMCA.Common.UI assembly) render populated markup for the render/axe E2E scan.
        builder.Services.AddScoped<NotificationState>();
        builder.Services.AddScoped<INotificationInboxUIService, StubNotificationInboxUIService>();
        builder.Services.AddScoped<IPushNotificationUIService, StubPushNotificationUIService>();

        // One empty UI module whose Assembly is the gallery, so the shared Router (Routes.razor)
        // discovers the gallery's own /components page alongside the real Login/Register pages, and
        // contributes a few nav links so the host is browsable when run interactively.
        builder.Services.AddSingleton<IUIModule, GalleryUIModule>();

        // Registers ApiSettings/LayoutSettings binding, the "APIClient" HttpClient, and the remaining
        // shared UI services. The in-memory Api:ApiEndpoint (appsettings.json) satisfies validation.
        // The client is never actually invoked because IAuthUIService is stubbed.
        builder.Services.AddUIShared(builder.Configuration);

        var app = builder.Build();

        // Request localization mirrors the real hosts' allowlist (ADR-027) and additionally enables the
        // qps-Ploc pseudo-locale UNCONDITIONALLY: this host is unpackaged test infrastructure (never
        // deployed), and the pseudo pass here is a required CI gate (PseudoLocalizationE2ETests, the
        // rubric §27 resource-round-trip + text-expansion evidence). Do not copy this into a real host.
        // Production keeps qps-Ploc Development-only via UseCommonRequestLocalization.
        string[] galleryCultures = [.. SupportedCultures.All, SupportedCultures.PseudoLocale];
        app.UseRequestLocalization(options => options
            .SetDefaultCulture(SupportedCultures.Default)
            .AddSupportedCultures(galleryCultures)
            .AddSupportedUICultures(galleryCultures));

        // The fake scheme + authorization middleware enforce the notification pages' [Authorize]
        // endpoint metadata (WebApplication inserts UseRouting ahead of these automatically).
        app.UseAuthentication();
        app.UseAuthorization();

        // Razor Component endpoints carry anti-forgery metadata — the middleware must be present even
        // though the gallery's interactive forms never POST over HTTP.
        app.UseAntiforgery();

        // MapStaticAssets likewise defaults the endpoints manifest filename to the entry assembly, so
        // resolve the gallery's own manifest explicitly for the same in-process self-host reason.
        app.MapStaticAssets(
            Path.Combine(baseDir, $"{galleryAssemblyName}.staticwebassets.endpoints.json"));

        app.MapGet("/health", () => Results.Ok("Healthy"));

        // The real Login/Register/Home pages live in the MMCA.Common.UI assembly; the gallery's own
        // pages (e.g. /components) come from App's assembly automatically.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(MMCA.Common.UI._Imports).Assembly);

        return app;
    }
}
