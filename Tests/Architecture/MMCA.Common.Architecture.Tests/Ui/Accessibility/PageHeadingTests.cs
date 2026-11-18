using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui.Accessibility;

/// <summary>
/// The framework's own one-h1-per-page gate (rubric section 20), driven by the shared
/// <see cref="PageHeadingTestsBase"/>: every routable page under MMCA.Common's <c>Source/</c> (the
/// sign-in, notification, home and error pages both consumers render) carries exactly one h1.
/// </summary>
public sealed class PageHeadingTests : PageHeadingTestsBase
{
    protected override string RepoRoot { get; } = ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx");

    // A floor, not an equality: 15 routable pages under Source/ when the gate landed (14 in UI, 1 in
    // UI.Web). Fewer means the scan root moved.
    protected override int MinimumRoutablePages => 12;

    // The OAuth return page runs unattended: it shows a spinner while it completes the sign-in and
    // navigates away, or an error alert with a way back to /login. It has no title to announce.
    protected override IReadOnlyCollection<string> PagesWithoutHeading { get; } =
        ["Presentation/MMCA.Common.UI/Pages/Auth/OAuthComplete.razor"];

    /// <summary>
    /// Keeps <see cref="PageHeadingTestsBase.FrameworkHeadingComponents"/> true: each component the
    /// list names still renders an h1, either itself or through <c>PageHeader</c>, so a consumer page
    /// that relies on one is not exempted on a stale promise.
    /// </summary>
    [Fact]
    public void EveryFrameworkHeadingComponent_StillRendersAnH1()
    {
        var uiRoot = Path.Combine(RepoRoot, "Source", "Presentation", "MMCA.Common.UI");
        var components = Directory
            .EnumerateFiles(uiRoot, "*.razor", SearchOption.AllDirectories)
            .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToLookup(static p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal);

        var broken = FrameworkHeadingComponents
            .Where(name => components[name].Take(2).Count() != 1
                || ArchitectureRules.PageHeadingViolation(
                    name,
                    File.ReadAllText(components[name].Single()),
                    string.Equals(name, "PageHeader", StringComparison.Ordinal) ? [] : ["PageHeader"]) is not null)
            .ToList();

        broken.Should().BeEmpty(
            because: "every framework heading component must render exactly one h1 (its own, or PageHeader's), or consumer pages that use it lose theirs");
    }
}
