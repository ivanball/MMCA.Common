using Microsoft.Extensions.Localization;

namespace MMCA.Common.API.Localization;

/// <summary>
/// Default <see cref="IErrorLocalizer"/>: resolves an error code against the registered
/// <see cref="ErrorResourceSource"/>s using the current UI culture, module sources first (in
/// registration order) and the framework's own source last, so a module's translation of a code is
/// never shadowed by Common's (the more specific source wins; Common is the fallback). It returns the
/// caller's English message when the code is empty or unknown to every source.
/// </summary>
/// <param name="sources">All registered error resource sources, in registration order.</param>
internal sealed class ErrorLocalizer(IEnumerable<ErrorResourceSource> sources) : IErrorLocalizer
{
    private readonly IReadOnlyList<ErrorResourceSource> _sources =
        [.. sources.Where(source => !source.IsFramework), .. sources.Where(source => source.IsFramework)];

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
