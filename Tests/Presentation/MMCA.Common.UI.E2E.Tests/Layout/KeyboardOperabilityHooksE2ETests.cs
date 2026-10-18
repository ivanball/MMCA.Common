using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.Workflows.Accessibility;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// Pins the extension points a consumer needs when its pages sit behind sign-in or its dialog
/// needs arranged data: every inherited fact awaits <c>ArrangeAsync</c> before it navigates and
/// <c>CleanupAsync</c> after it, and every page load goes through <c>NavigateAsync</c>. The overrides below refuse to navigate before the
/// arrange step ran and to hand out the dialog opener before a navigation went through the hook, so
/// every inherited fact fails if either hook is bypassed.
/// </summary>
[Collection(GalleryE2ECollection.Name)]
public sealed class KeyboardOperabilityHooksE2ETests : KeyboardOperabilityTestsBase
{
    private bool _arranged;

    private int _navigations;

    public KeyboardOperabilityHooksE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright) =>
        ArgumentNullException.ThrowIfNull(gallery);

    protected override IReadOnlyList<string> PagePaths => ["/components"];

    protected override string DialogPagePath => "/components";

    protected override ILocator DialogOpener
    {
        get
        {
            _navigations.Should().BePositive("the dialog page must be loaded through NavigateAsync");
            return Page.GetByRole(AriaRole.Button, new() { Name = "Delete sample" });
        }
    }

    protected override Task ArrangeAsync()
    {
        _arranged = true;
        return Task.CompletedTask;
    }

    protected override Task CleanupAsync()
    {
        _arranged.Should().BeTrue("CleanupAsync undoes ArrangeAsync, so it must run after it");
        _arranged = false;
        return Task.CompletedTask;
    }

    protected override Task NavigateAsync(string path)
    {
        _arranged.Should().BeTrue("ArrangeAsync must run before the first navigation");
        _navigations++;
        return base.NavigateAsync(path);
    }
}
