using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.UI.Components;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Components;

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
    public void SelectingACulture_DelegatesToTheApplier_WithTheCurrentPathAsReturnPath()
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        navigation.NavigateTo("/sessions?page=2");

        SpanishItem().Click();

        _applier.Culture.Should().Be("es");
        _applier.ReturnPath.Should().Be("/sessions?page=2");
    }

    // The switcher must not reintroduce a navigation of its own: the applier owns landing the user back
    // on the page, and on a hybrid head that is a WebView reload, not a server redirect.
    [Fact]
    public void SelectingACulture_DoesNotNavigateOnItsOwn()
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        SpanishItem().Click();

        _applier.Culture.Should().Be("es", "the applier ran, so an empty history is not a no-op test");
        navigation.History.Should().BeEmpty();
    }

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
        cut.Find("button").Click();

        return [.. providers.Popover.FindAll("div.mud-menu-item")];
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
