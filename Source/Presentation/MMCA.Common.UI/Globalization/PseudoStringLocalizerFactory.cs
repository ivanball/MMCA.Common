using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Globalization;

/// <summary>
/// An <see cref="IStringLocalizerFactory"/> decorator that wraps every localizer the inner factory
/// produces in a <see cref="PseudoStringLocalizer"/> (ADR-027 §8). Because <c>StringLocalizer&lt;T&gt;</c>
/// resolves its backing localizer through this factory, decorating the factory pseudo-localizes every
/// <see cref="IStringLocalizer{T}"/> and <see cref="IStringLocalizer"/> in the host at once.
/// </summary>
public sealed class PseudoStringLocalizerFactory(IStringLocalizerFactory inner) : IStringLocalizerFactory
{
    /// <inheritdoc />
    public IStringLocalizer Create(Type resourceSource) =>
        new PseudoStringLocalizer(inner.Create(resourceSource));

    /// <inheritdoc />
    public IStringLocalizer Create(string baseName, string location) =>
        new PseudoStringLocalizer(inner.Create(baseName, location));
}
