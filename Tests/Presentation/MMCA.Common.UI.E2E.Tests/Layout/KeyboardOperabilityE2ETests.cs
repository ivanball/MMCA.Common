using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.Workflows.Accessibility;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// Runs the shipped <see cref="KeyboardOperabilityTestsBase"/> against the self-hosted gallery, the
/// same way a consumer subclasses it against its own app: the shared <c>MainLayout</c> skip link and
/// focus indicators on <c>/components</c> and <c>/login</c>, and the shared <c>DeleteConfirmation</c>
/// dialog behind the gallery's "Delete sample" button. The collection is the gallery's (browser plus
/// in-process host), which also points <c>E2ETestConfiguration.BaseUrl</c> at the gallery.
/// </summary>
[Collection(GalleryE2ECollection.Name)]
public sealed class KeyboardOperabilityE2ETests : KeyboardOperabilityTestsBase
{
    public KeyboardOperabilityE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright) =>
        ArgumentNullException.ThrowIfNull(gallery);

    protected override IReadOnlyList<string> PagePaths => ["/components", "/login"];

    protected override string DialogPagePath => "/components";

    protected override ILocator DialogOpener => Page.GetByRole(AriaRole.Button, new() { Name = "Delete sample" });
}
