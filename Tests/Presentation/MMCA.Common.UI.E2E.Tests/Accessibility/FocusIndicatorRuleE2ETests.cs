using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.Workflows.Accessibility;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace MMCA.Common.UI.E2E.Tests.Accessibility;

/// <summary>
/// L193: the focus-indicator fact of <see cref="KeyboardOperabilityTestsBase"/> accepts a box-shadow
/// only when it differs from the same element's unfocused box-shadow. A Tab stop that always carries a
/// resting shadow (an elevated filled button) with its outline removed on focus shows no focus
/// indicator, and must fail. The fact is driven over inline fixture pages by a private subclass whose
/// <c>NavigateAsync</c> loads the fixture instead of a gallery route, so xUnit does not collect its
/// inherited facts as tests of their own.
/// </summary>
[Collection(GalleryE2ECollection.Name)]
public sealed class FocusIndicatorRuleE2ETests
{
    private const string Buttons = "<button>One</button><button>Two</button><button>Three</button>";

    private readonly PlaywrightFixture _playwright;

    public FocusIndicatorRuleE2ETests(PlaywrightFixture playwright) => _playwright = playwright;

    [Fact]
    public async Task ARestingBoxShadow_WithTheOutlineRemovedOnFocus_IsNotAFocusIndicator()
    {
        var page = Fixture("button { border: 0; outline: none; box-shadow: 0 2px 4px rgb(0, 0, 0); } button:focus, button:focus-visible { outline: none; }");

        var act = () => RunFocusIndicatorFactAsync(page);

        (await act.Should().ThrowAsync<XunitException>())
            .Which.Message.Should().Contain("Tab stop 1").And.Contain("unfocused box-shadow");
    }

    [Theory]
    [InlineData("button { border: 0; outline: none; box-shadow: none; } button:focus-visible { box-shadow: 0 0 0 3px rgb(0, 0, 255); }")]
    [InlineData("button { border: 0; outline: none; box-shadow: 0 2px 4px rgb(0, 0, 0); } button:focus-visible { box-shadow: 0 0 0 3px rgb(0, 0, 255); }")]
    [InlineData("button { border: 0; outline: none; box-shadow: 0 2px 4px rgb(0, 0, 0); } button:focus-visible { outline: 2px solid rgb(0, 0, 255); }")]
    public async Task AFocusOnlyRingOrShadow_IsAFocusIndicator(string css)
    {
        var page = Fixture(css);

        var act = () => RunFocusIndicatorFactAsync(page);

        await act.Should().NotThrowAsync();
    }

    private static string Fixture(string css) =>
        $"<!doctype html><html lang=\"en\"><head><title>Focus fixture</title><style>{css}</style></head><body><main>{Buttons}</main></body></html>";

    private async Task RunFocusIndicatorFactAsync(string html)
    {
        var probe = new FixturePageProbe(_playwright, html);
        await probe.InitializeAsync();
        try
        {
            await probe.FocusedElement_ShowsVisibleFocusIndicator();
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }

    private sealed class FixturePageProbe(PlaywrightFixture playwright, string html) : KeyboardOperabilityTestsBase(playwright)
    {
        protected override IReadOnlyList<string> PagePaths => ["fixture"];

        protected override string DialogPagePath => "fixture";

        protected override ILocator DialogOpener => Page.Locator("button").First;

        protected override Task NavigateAsync(string path) => Page.SetContentAsync(html);
    }
}
