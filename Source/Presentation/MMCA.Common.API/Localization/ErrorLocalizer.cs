using Microsoft.Extensions.Localization;

namespace MMCA.Common.API.Localization;

/// <summary>
/// Default <see cref="IErrorLocalizer"/>: resolves an error code against an ordered set of registered
/// <see cref="ErrorResourceSource"/>s (Common first, then modules) using the current UI culture, and
/// falls back to the caller's English message when the code is empty or unknown to every source.
/// </summary>
/// <param name="sources">All registered error resource sources, in registration order.</param>
internal sealed class ErrorLocalizer(IEnumerable<ErrorResourceSource> sources) : IErrorLocalizer
{
    private readonly IReadOnlyList<ErrorResourceSource> _sources = [.. sources];

    /// <inheritdoc />
    public string Localize(string code, string fallbackMessage)
    {
        if (string.IsNullOrEmpty(code))
        {
            return fallbackMessage;
        }

        foreach (var source in _sources)
        {
            LocalizedString localized = source.Localizer[code];
            if (!localized.ResourceNotFound)
            {
                return localized.Value;
            }
        }

        return fallbackMessage;
    }
}
