namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Accessibility guard (rubric section 21), driven by
/// <see cref="ArchitectureRules.RoutablePagesRenderExactlyOneH1"/>: every routable page under the
/// repo's <c>Source/</c> renders exactly one level-one heading. Authored once here (lifted from
/// MMCA.ADC's <c>PageHeadingTests</c>) and re-run as a thin subclass in each repo, which supplies
/// <see cref="RepoRoot"/> and <see cref="MinimumRoutablePages"/>; the floor proves the scan actually
/// walked the UI tree.
/// <para>
/// A page that renders one of <see cref="HeadingComponents"/> gets its h1 from that component and
/// must carry none of its own. The default is <see cref="FrameworkHeadingComponents"/>, MMCA.Common's
/// own components that render the page title as an h1, so a consumer whose pages use only those needs
/// no override; a consumer with its own heading component appends it:
/// <c>[.. FrameworkHeadingComponents, "MyPageTitle"]</c>. Reviewed exemptions go in
/// <see cref="BranchedHeadingPages"/> and <see cref="PagesWithoutHeading"/>, both empty by default and
/// both checked for staleness.
/// </para>
/// </summary>
public abstract class PageHeadingTestsBase
{
    /// <summary>
    /// MMCA.Common's components whose render supplies the page h1: <c>PageHeader</c> (a
    /// <c>MudText Typo.h4 HtmlTag="h1"</c>), <c>ListPageShell</c> (which renders <c>PageHeader</c>),
    /// and the administration pages a consumer mounts behind its own route.
    /// </summary>
    public static IReadOnlyList<string> FrameworkHeadingComponents { get; } =
    [
        "PageHeader",
        "ListPageShell",
        "FeatureFlagAdmin",
        "MachineClientAdmin",
        "RoleAdminEdit",
        "RoleAdminList",
        "SupportAccessLog",
        "UserAdminList",
    ];

    /// <summary>The repo root, typically <c>ArchitectureMapBase.FindRepoRoot("MyRepo.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The fewest routable pages the scan must reach, so a moved root cannot pass vacuously.</summary>
    protected abstract int MinimumRoutablePages { get; }

    /// <summary>The directories scanned. Defaults to <c>Source</c> under <see cref="RepoRoot"/>.</summary>
    protected virtual IReadOnlyCollection<string> MarkupRoots => [Path.Combine(RepoRoot, "Source")];

    /// <summary>Component tag names whose render supplies the page h1. Defaults to <see cref="FrameworkHeadingComponents"/>.</summary>
    protected virtual IReadOnlyCollection<string> HeadingComponents => FrameworkHeadingComponents;

    /// <summary>
    /// Pages whose h1 sits in mutually exclusive render branches (a loading or not-found title and the
    /// loaded title), keyed by the path relative to its markup root (forward slashes), valued by the
    /// exact own-h1 count. Empty by default.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, int> BranchedHeadingPages { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Pages reviewed as rendering no heading at all (an unattended redirect page), by path relative to
    /// its markup root (forward slashes). Empty by default and meant to stay short.
    /// </summary>
    protected virtual IReadOnlyCollection<string> PagesWithoutHeading => [];

    [Fact]
    public void EveryRoutablePage_RendersExactlyOneH1() =>
        ArchitectureRules.RoutablePagesRenderExactlyOneH1(
            MarkupRoots,
            MinimumRoutablePages,
            HeadingComponents,
            BranchedHeadingPages,
            PagesWithoutHeading);
}
