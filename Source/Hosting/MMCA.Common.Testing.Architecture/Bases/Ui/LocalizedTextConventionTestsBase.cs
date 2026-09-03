namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Localized-text convention fitness function (ADR-027 / rubric §27): user-visible literals must not
/// be hard-coded in <c>.razor</c>/<c>.razor.cs</c> under <c>Source/</c> — snackbar messages, page
/// <c>Title</c> properties, <c>&lt;PageTitle&gt;</c> markup, and breadcrumb labels must resolve
/// through <c>IStringLocalizer</c> resources so every visible string follows the selected language.
/// Authored once here and re-run as a thin subclass in each repo. Deliberate literals (brand names,
/// developer-only samples) are exempted per line with an <c>i18n: allow</c> comment or per file via
/// <see cref="AllowedFiles"/>. Pairs with <see cref="LocalizationResourceTestsBase"/>: this gate keeps
/// strings OUT of markup/code, that gate keeps the extracted resources fully translated.
/// </summary>
public abstract class LocalizedTextConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Minimum number of razor files the scan must discover — a non-vacuous guard so a wrong scan root
    /// cannot let the gate pass having checked nothing. Override to the repo's approximate razor count.
    /// </summary>
    protected virtual int MinimumScannedFiles => 1;

    /// <summary>
    /// Relative path suffixes (with <c>/</c> separators) excluded from the scan entirely, for files
    /// that are deliberately literal (e.g. generated stubs). Prefer the per-line <c>i18n: allow</c>
    /// marker; use this only when a whole file is exempt.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedFiles => [];

    [Fact]
    public void UserVisibleText_IsLocalized()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        ArchitectureRules.UserVisibleTextIsLocalized(
            Path.Combine(repoRoot, "Source"),
            AllowedFiles,
            MinimumScannedFiles);
    }
}
