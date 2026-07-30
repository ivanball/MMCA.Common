using System.Globalization;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.UI.Maui.Globalization;

/// <summary>
/// The single storage + activation path for the culture on a MAUI Blazor Hybrid head (ADR-027).
/// A hybrid head has no ASP.NET request pipeline, so none of the web machinery applies: there is no
/// culture cookie to write and nothing reads <c>CookieRequestCultureProvider</c>. The active culture is
/// process state (<see cref="CultureInfo.DefaultThreadCurrentUICulture"/>) and the choice is persisted
/// in device preferences so it survives an app restart.
/// <para>
/// Deliberately reads and writes <c>Preferences.Default</c> directly rather than going through
/// <c>IDevicePreferences</c>: that contract is async-only, and the startup restore runs from
/// <c>IMauiInitializeService.Initialize</c>, a synchronous hook. One value must have exactly one
/// storage path, so both sides use this class rather than splitting across the two.
/// </para>
/// </summary>
internal static class MauiCultureStore
{
    /// <summary>
    /// The device-preference key. Outside the <c>IDevicePreferences</c> prefix on purpose (see the
    /// type remarks); changing it silently resets every installed app to the device locale.
    /// </summary>
    private const string PreferenceKey = "mmca.culture";

    /// <summary>Persists the user's explicit choice. Best-effort, mirroring the rest of the layer.</summary>
    /// <param name="culture">A culture from <c>SupportedCultures.All</c>.</param>
    public static void Save(string culture) => Preferences.Default.Set(PreferenceKey, culture);

    /// <summary>
    /// Resolves the culture to start under, in the same precedence order the web heads get from request
    /// localization: the stored explicit choice (the cookie's analogue), then the device locale matched
    /// by language (<c>Accept-Language</c>'s analogue, so an <c>es-MX</c> phone gets <c>es</c>), then
    /// the framework default.
    /// </summary>
    public static string Resolve()
    {
        var stored = Preferences.Default.Get<string?>(PreferenceKey, null);

        return SupportedCultures.IsSupported(stored)
            ? stored!
            : SupportedCultures.ResolveClosest(CultureInfo.CurrentUICulture.Name);
    }

    /// <summary>
    /// Makes <paramref name="culture"/> the active culture for the process, by setting the thread
    /// defaults and <b>only</b> the thread defaults.
    /// <para>
    /// Assigning <see cref="CultureInfo.CurrentCulture"/> / <see cref="CultureInfo.CurrentUICulture"/>
    /// here would break runtime switching, which is why this deliberately does not. Those setters write
    /// to an <c>AsyncLocal</c>, so the value flows with the <see cref="ExecutionContext"/> and is
    /// RESTORED every time that context is re-entered, taking precedence over the thread defaults. The
    /// startup restore (<see cref="MauiCultureInitializer"/>) runs before any window exists, so the
    /// context it writes to is the one every later dispatch descends from, including the Blazor
    /// renderer's. A later switch could then set the defaults to "es" and still re-render in the
    /// startup culture forever: the reload re-enters that context, the AsyncLocal wins, and the app is
    /// stuck on whatever language it launched in. A web head never hits this because request
    /// localization sets the culture per request, inside each request's own context.
    /// </para>
    /// <para>
    /// Nothing needs the calling thread set explicitly. As long as no code path ever assigns
    /// <c>Current*</c>, no thread materializes a culture of its own, so the defaults govern every
    /// thread and every context, and a switch takes effect everywhere at once.
    /// </para>
    /// </summary>
    /// <param name="culture">A culture from <c>SupportedCultures.All</c>.</param>
    public static void ApplyToProcess(string culture)
    {
        var cultureInfo = new CultureInfo(culture);

        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
    }
}
