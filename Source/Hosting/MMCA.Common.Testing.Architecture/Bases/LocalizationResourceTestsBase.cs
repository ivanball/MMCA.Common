namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Opt-in translation-coverage fitness gate (ADR-027). A repo that ships localized <c>.resx</c> resources
/// subclasses this and supplies its non-default <see cref="RequiredCultures"/>; the build then fails if any
/// base <c>.resx</c> under <c>Source/</c> lacks a complete, non-empty sibling for a required culture, so a
/// new English string can never ship without its translation. Single-locale repos need not subclass it (the
/// underlying rule is vacuous for an empty list).
/// </summary>
public abstract class LocalizationResourceTestsBase
{
    /// <summary>The non-default cultures every base <c>.resx</c> must fully translate (e.g. <c>["es"]</c>).</summary>
    protected abstract IReadOnlyCollection<string> RequiredCultures { get; }

    [Fact]
    public void Translations_AreComplete_ForEveryRequiredCulture() =>
        ArchitectureRules.ResourceTranslationsAreComplete(RequiredCultures);
}
