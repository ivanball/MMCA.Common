using System.Globalization;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.UI.Globalization;

/// <summary>
/// An <see cref="IStringLocalizer"/> decorator that pseudo-localizes every resolved string when, and
/// only when, the current UI culture is <see cref="SupportedCultures.PseudoLocale"/> (ADR-027 §8).
/// Under every other culture it delegates unchanged to the wrapped localizer, so it is inert in
/// production (where the pseudo locale is never an activatable request culture).
/// </summary>
public sealed class PseudoStringLocalizer(IStringLocalizer inner) : IStringLocalizer
{
    /// <summary>Whether the current UI culture activates pseudo-localization.</summary>
    private static bool IsPseudoActive =>
        SupportedCultures.IsPseudoLocale(CultureInfo.CurrentUICulture.Name);

    /// <inheritdoc />
    public LocalizedString this[string name]
    {
        get
        {
            var localized = inner[name];
            return IsPseudoActive
                ? new LocalizedString(name, PseudoLocalizer.Transform(localized.Value), localized.ResourceNotFound, localized.SearchedLocation ?? string.Empty)
                : localized;
        }
    }

    /// <inheritdoc />
    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            if (!IsPseudoActive)
            {
                return inner[name, arguments];
            }

            // Transform the raw template (placeholders preserved) and only then format with the
            // arguments, so accents/padding never corrupt the substituted values.
            var template = inner[name];
            var transformed = PseudoLocalizer.Transform(template.Value);
            var formatted = string.Format(CultureInfo.CurrentCulture, transformed, arguments);
            return new LocalizedString(name, formatted, template.ResourceNotFound, template.SearchedLocation ?? string.Empty);
        }
    }

    /// <inheritdoc />
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var all = inner.GetAllStrings(includeParentCultures);
        return IsPseudoActive
            ? all.Select(s => new LocalizedString(s.Name, PseudoLocalizer.Transform(s.Value), s.ResourceNotFound, s.SearchedLocation ?? string.Empty))
            : all;
    }
}
