using System.Collections;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace MMCA.Common.UI.Globalization;

/// <summary>
/// The MudBlazor <see cref="ILocalizationInterceptor"/> that <c>AddUIShared</c> installs in place of
/// MudBlazor's <c>DefaultLocalizationInterceptor</c> (ADR-027). The resolution order is the default
/// one: an English UI culture, or no <see cref="MudLocalizer"/>, reads MudBlazor's built-in strings;
/// any other culture asks the <see cref="MudLocalizer"/> (<see cref="ResxMudLocalizer"/> here) and
/// falls back to the built-in string when the key is untranslated. The one difference is HOW the
/// built-in strings are read.
/// <para>
/// MudBlazor 9.7+ reads them by assigning <see cref="CultureInfo.CurrentUICulture"/> to the invariant
/// culture and then assigning the previous value back. Both assignments write the culture into an
/// <c>AsyncLocal</c>, and that "restore" leaves the calling thread's <c>ExecutionContext</c> carrying
/// an explicit culture from then on. On a MAUI Blazor Hybrid head the renderer dispatches on the
/// process's main thread, whose context nothing ever resets, so the first MudBlazor chrome string a
/// page renders pins the app to the language it launched in: a later switch sets the thread defaults
/// (the only thing <c>MauiCultureApplier</c> may set, see ADR-027 Decision 10), reloads the WebView,
/// and still renders the old language because the pinned <c>AsyncLocal</c> wins over the defaults.
/// A web head never notices: request localization sets the culture per request and a browser reload
/// tears the WASM runtime down, so nothing long-lived is pinned.
/// </para>
/// <para>
/// This interceptor reads the same embedded resource through a <see cref="ResourceManager"/> with an
/// explicit invariant culture, which is what MudBlazor's swap was for (no satellite-assembly probing
/// under non-English cultures) without any culture assignment. Registered with MudBlazor's
/// <c>AddLocalizationInterceptor</c>, which replaces the default regardless of whether
/// <c>AddMudServices</c> ran before or after <c>AddUIShared</c>.
/// </para>
/// </summary>
/// <param name="mudLocalizer">The app-supplied translations; <see langword="null"/> when none is registered.</param>
internal sealed class InvariantMudLocalizationInterceptor(MudLocalizer? mudLocalizer = null)
    : AbstractLocalizationInterceptor(BuiltInStrings.Instance, mudLocalizer)
{
    /// <inheritdoc />
    public override LocalizedString Handle(string key, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);

        // Same test MudBlazor's default applies: the parent's language, so "en-US" counts as English
        // while the neutral "en" (whose parent is the invariant culture) goes through the MudLocalizer
        // like every other culture and lands on the built-in string only as a fallback.
        var isEnglish = string.Equals(
            CultureInfo.CurrentUICulture.Parent.TwoLetterISOLanguageName,
            "en",
            StringComparison.OrdinalIgnoreCase);

        if (MudLocalizer is null || isEnglish)
        {
            return BuiltIn(key, arguments);
        }

        var translated = MudLocalizer[key, arguments];
        return translated.ResourceNotFound ? BuiltIn(key, arguments) : translated;
    }

    // The no-argument indexer is deliberate: formatting a value that contains literal braces with an
    // empty argument list would throw, and MudBlazor's own strings are looked up that way too.
    private LocalizedString BuiltIn(string key, object[] arguments) =>
        arguments.Length > 0 ? Localizer[key, arguments] : Localizer[key];

    /// <summary>
    /// MudBlazor's built-in English strings, read from its embedded <c>LanguageResource</c> under the
    /// invariant culture. The resource is addressed by manifest name because the generated
    /// <c>LanguageResource</c> class is internal to MudBlazor.
    /// </summary>
    private sealed class BuiltInStrings : IStringLocalizer
    {
        private const string ResourceBaseName = "MudBlazor.Resources.LanguageResource";

        private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(MudLocalizer).Assembly);

        private BuiltInStrings()
        {
        }

        public static BuiltInStrings Instance { get; } = new();

        public LocalizedString this[string name]
        {
            get
            {
                var value = Resources.GetString(name, CultureInfo.InvariantCulture);
                return new LocalizedString(name, value ?? name, resourceNotFound: value is null);
            }
        }

        public LocalizedString this[string name, params object[] arguments]
        {
            get
            {
                var format = Resources.GetString(name, CultureInfo.InvariantCulture);
                var value = format is null
                    ? name
                    : string.Format(CultureInfo.CurrentCulture, format, arguments);
                return new LocalizedString(name, value, resourceNotFound: format is null);
            }
        }

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            var set = Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
            if (set is null)
            {
                yield break;
            }

            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    yield return new LocalizedString(key, value, resourceNotFound: false);
                }
            }
        }
    }
}
