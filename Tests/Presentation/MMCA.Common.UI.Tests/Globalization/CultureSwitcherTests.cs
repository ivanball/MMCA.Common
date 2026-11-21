using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.UI.Globalization;
using MMCA.Common.UI.Services.Culture;
using MMCA.Common.UI.Tests.Infrastructure;

namespace MMCA.Common.UI.Tests.Globalization;

/// <summary>
/// Covers <see cref="CultureSwitcher"/> (ADR-027). The regression guard here is that the switcher
/// delegates to <see cref="ICultureApplier"/> instead of navigating to <c>/culture/set</c> itself: that
/// URL is a server endpoint, so a hard-coded navigation left MAUI Blazor Hybrid heads (which host no
/// ASP.NET pipeline) routing it through the Blazor router onto the not-found page.
/// </summary>
public sealed class CultureSwitcherTests : BunitTestBase
{
    private readonly RecordingCultureApplier _applier = new();

    public CultureSwitcherTests() => Services.AddScoped<ICultureApplier>(_ => _applier);

    // The pseudo locale is offered only when IHostEnvironment reports Development, and no such service
    // is registered here (nor on a MAUI head), so the allowlist is the whole menu.
    [Fact]
    public void OffersEverySupportedCulture()
    {
        var items = OpenMenu();

        items.Should().HaveCount(SupportedCultures.All.Count);
        items.Select(i => i.TextContent).Should().Contain("Español");
    }

    [Fact]
    public async Task SelectingACulture_DelegatesToTheApplier_WithTheCurrentPathAsReturnPath()
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        navigation.NavigateTo("/sessions?page=2");

        await SpanishItem().ClickAsync();

        _applier.Culture.Should().Be("es");
        _applier.ReturnPath.Should().Be("/sessions?page=2");
    }

    // The switcher must not reintroduce a navigation of its own: the applier owns landing the user back
    // on the page, and on a hybrid head that is a WebView reload, not a server redirect.
    [Fact]
    public async Task SelectingACulture_DoesNotNavigateOnItsOwn()
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        await SpanishItem().ClickAsync();

        _applier.Culture.Should().Be("es", "the applier ran, so an empty history is not a no-op test");
        navigation.History.Should().BeEmpty();
    }

    // Selecting a culture must be awaited via ClickAsync: MudMenuItem's OnClick is an async
    // EventCallback, so the synchronous Click() returns once the handler is dispatched rather than
    // once it has run, and asserting on the applier straight after it loses the race under CI load.
    private IElement SpanishItem()
    {
        var items = OpenMenu();

        // Menu items follow SupportedCultures.All order (the switcher iterates it directly).
        var spanish = items[SupportedCultures.All.ToList().IndexOf("es")];
        spanish.TextContent.Should().Be("Español", "the index-to-culture mapping must stay honest");
        return spanish;
    }

    // MudMenu renders its items into the popover provider's tree only once opened, so the activator
    // click is required and the items are queried from the provider, not from the component under test.
    private IReadOnlyList<IElement> OpenMenu()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<CultureSwitcher>(_ => { });
        cut.Find(".mud-menu").Click();

        return [.. providers.Popover.FindAll("div.mud-menu-item")];
    }

    // Run 16 D5: the Android WebView gives a button with aria-haspopup no name from aria-label, so
    // the name has to be text inside the one interactive activator (a nested button would break
    // axe nested-interactive).
    [Fact]
    public void Activator_IsNamedByItsOwnText_WithNoNestedButton()
    {
        RenderMudProviders();

        var cut = RenderUnderTest<CultureSwitcher>(_ => { });

        var activator = cut.Find("div[role=button][aria-haspopup=menu]");
        activator.TextContent.Should().Contain("Language");
        activator.HasAttribute("aria-label").Should().BeFalse();
        cut.FindAll("button").Should().BeEmpty();
    }

    // An accessibility click (TalkBack's double-tap) lands on the role=button element itself, not on
    // the content inside it, so the menu has to open from a click on exactly that element.
    [Fact]
    public void ClickOnTheActivatorElementItself_OpensTheMenu()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<CultureSwitcher>(_ => { });

        cut.Find("div[role=button][aria-haspopup=menu]").Click();

        providers.Popover.FindAll("div.mud-menu-item").Should().NotBeEmpty();
    }

    // Run 18 D9: the Language control was never reached by Tab, because MudBlazor renders the
    // activator wrapper display: contents, and a display: contents box takes no focus. The wrapper is
    // the one control (tabindex 0, named by its text), and app.css gives it a box through the
    // culture-switcher class on the menu root.
    [Fact]
    public void Activator_IsInTheTabOrder_AndTheMenuRootCarriesTheStylingHook()
    {
        RenderMudProviders();

        var cut = RenderUnderTest<CultureSwitcher>(_ => { });

        var activator = cut.Find(".mud-menu.culture-switcher > div.mud-menu-activator[role=button]");
        activator.GetAttribute("tabindex").Should().Be("0");
        activator.TextContent.Should().Contain("Language");
    }

    // Run 19: the rule has to out-rank MudBlazor's .mud-menu>div.mud-menu-activator{display:contents}
    // (0,2,1); the Run 18 selector .culture-switcher > .mud-menu-activator (0,2,0) lost in the browser.
    // CultureSwitcherKeyboardE2ETests reads the computed display and real focus.
    [Fact]
    public void AppCss_GivesTheActivatorAFocusableBox()
    {
        var css = CssStylesheet.CssComment.Replace(CssStylesheet.ReadEmbedded("app.css"), string.Empty);

        var bodies = CssStylesheet.RuleBodies(
            css,
            selector => string.Equals(selector, ".mud-menu.culture-switcher > div.mud-menu-activator", StringComparison.Ordinal));

        bodies.Should().ContainSingle().Which.Should().Contain("display: inline-flex", "display: contents takes no focus");
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public void EnterOrSpaceOnTheActivator_OpensTheMenu(string key)
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<CultureSwitcher>(_ => { });

        cut.Find("div[role=button][aria-haspopup=menu]").KeyDown(new KeyboardEventArgs { Key = key });

        providers.Popover.FindAll("div.mud-menu-item").Should().NotBeEmpty();
    }

    private sealed class RecordingCultureApplier : ICultureApplier
    {
        public string? Culture { get; private set; }

        public string? ReturnPath { get; private set; }

        public Task ApplyAsync(string culture, string returnPath, CancellationToken cancellationToken = default)
        {
            Culture = culture;
            ReturnPath = returnPath;
            return Task.CompletedTask;
        }
    }
}
