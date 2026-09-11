using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.UI.Components.Lists;

namespace MMCA.Common.UI.Tests.Components.Lists;

/// <summary>
/// The keyboard contract of the shared card primitive behind both mobile list components. A card
/// with a click callback used to be a <c>div</c> carrying only <c>@onclick</c>: invisible to the tab
/// order, announced as nothing, and unreachable without a mouse (WCAG 2.1.1 / 4.1.2). A card with no
/// callback must stay exactly as inert as it was, so a read-only list does not fill the tab order.
/// </summary>
public sealed class ClickableCardTests : BunitTestBase
{
    [Fact]
    public void WithACallback_IsFocusableAndAnnouncedAsAButton()
    {
        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => { }))
            .AddChildContent("Row body"));

        var card = cut.Find(".mobile-list-card");
        card.GetAttribute("tabindex").Should().Be("0");
        card.GetAttribute("role").Should().Be("button");
    }

    [Fact]
    public void WithoutACallback_CarriesNoTabindexAndNoRole()
    {
        var cut = RenderUnderTest<ClickableCard>(p => p.AddChildContent("Row body"));

        var card = cut.Find(".mobile-list-card");
        card.HasAttribute("tabindex").Should().BeFalse();
        card.HasAttribute("role").Should().BeFalse();
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    [InlineData("Spacebar")]
    public void ActivationKeys_RaiseTheCallback(string key)
    {
        var activations = 0;

        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => activations++))
            .AddChildContent("Row body"));

        cut.Find(".mobile-list-card").KeyDown(new KeyboardEventArgs { Key = key });

        activations.Should().Be(1);
    }

    [Fact]
    public void OtherKeys_DoNotRaiseTheCallback()
    {
        var activations = 0;

        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => activations++))
            .AddChildContent("Row body"));

        var card = cut.Find(".mobile-list-card");
        card.KeyDown(new KeyboardEventArgs { Key = "Tab" });
        card.KeyDown(new KeyboardEventArgs { Key = "a" });
        card.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        activations.Should().Be(0);
    }

    [Fact]
    public void Click_StillRaisesTheCallback()
    {
        // The keyboard path is additive: the pointer behaviour the lists always had is unchanged.
        var activations = 0;

        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => activations++))
            .AddChildContent("Row body"));

        cut.Find(".mobile-list-card").Click();

        activations.Should().Be(1);
    }

    [Fact]
    public void AriaLabel_OverridesTheContentDerivedName()
    {
        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => { }))
            .Add(c => c.AriaLabel, "Open order 4711")
            .AddChildContent("4711"));

        cut.Find(".mobile-list-card").GetAttribute("aria-label").Should().Be("Open order 4711");
    }
}
