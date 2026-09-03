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

    /// <summary>
    /// Minimum number of base <c>.resx</c> files the scan must discover — a non-vacuous guard so a wrong
    /// scan root or a repo re-layout cannot let the gate pass having checked nothing. Override in the
    /// subclass to (a floor of) the repo's known localized-resource count; the default of zero skips the
    /// guard.
    /// </summary>
    protected virtual int MinimumBaseResources => 0;

    [Fact]
    public void Translations_AreComplete_ForEveryRequiredCulture() =>
        ArchitectureRules.ResourceTranslationsAreComplete(RequiredCultures, MinimumBaseResources);
}
