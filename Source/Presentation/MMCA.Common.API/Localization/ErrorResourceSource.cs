using Microsoft.Extensions.Localization;

namespace MMCA.Common.API.Localization;

/// <summary>
/// A registered resource source the <see cref="IErrorLocalizer"/> consults when resolving an error code
/// to localized text (ADR-027). Common registers one for its own error resources; each module registers
/// its own additively via <c>AddErrorResources&lt;TResource&gt;()</c>, so the localizer enumerates an
/// ordered set (Common first, then modules) and returns the first match.
/// </summary>
/// <param name="localizer">The <see cref="IStringLocalizer"/> backing this source's <c>.resx</c> set.</param>
public sealed class ErrorResourceSource(IStringLocalizer localizer)
{
    /// <summary>The localizer whose keyed resources map error codes to translated messages.</summary>
    public IStringLocalizer Localizer { get; } = localizer;
}
