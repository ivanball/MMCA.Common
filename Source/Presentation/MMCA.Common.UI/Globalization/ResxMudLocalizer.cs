using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.Globalization;

/// <summary>
/// Localizes MudBlazor's built-in component text (pager, filter menus, pickers, close buttons)
/// from the <see cref="MudTranslations"/> resource pair (ADR-027). MudBlazor's
/// <c>DefaultLocalizationInterceptor</c> consults this localizer only for non-English cultures and
/// falls back to its built-in English strings whenever the returned value reports
/// <see cref="LocalizedString.ResourceNotFound"/>, so untranslated keys degrade gracefully.
/// Because resolution flows through the DI <see cref="IStringLocalizerFactory"/>, the
/// <see cref="PseudoStringLocalizerFactory"/> decorator applies here too: under the development-only
/// <c>qps-Ploc</c> culture, MudBlazor chrome pseudo-localizes along with application text.
/// </summary>
internal sealed class ResxMudLocalizer(IStringLocalizer<MudTranslations> localizer) : MudLocalizer
{
    public override LocalizedString this[string key] => localizer[key];
}
