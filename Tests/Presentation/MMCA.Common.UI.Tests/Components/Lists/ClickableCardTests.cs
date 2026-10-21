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
    public void EnterOnANestedControl_DoesNotActivateTheCard()
    {
        // M130: Enter on a button inside the card belongs to that button, not to the card.
        var activations = 0;
        var innerPresses = 0;

        // The nested control handles its own keydown, as a MudButton or a link would.
        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => activations++))
            .AddChildContent(builder =>
            {
                builder.OpenElement(0, "button");
                builder.AddAttribute(1, "id", "inner");
                builder.AddAttribute(2, "type", "button");
                builder.AddAttribute(3, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(this, () => innerPresses++));
                builder.AddContent(4, "Delete");
                builder.CloseElement();
            }));

        cut.Find("#inner").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        innerPresses.Should().Be(1);
        activations.Should().Be(0, "a keydown that starts on a nested control must not reach the card");

        cut.Find(".mobile-list-card").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        activations.Should().Be(1, "Enter on the focused card itself still activates it");
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
    public void InteractiveCard_IsNamedByItsContent_WithNoAriaLabel()
    {
        // WCAG 2.5.3 (Label in Name): an aria-label replacing the visible text would fail axe's
        // label-content-name-mismatch, so the card never carries one and its content is its name.
        var cut = RenderUnderTest<ClickableCard>(p => p
            .Add(c => c.OnActivate, EventCallback.Factory.Create(this, () => { }))
            .AddChildContent("Order 4711"));

        var card = cut.Find(".mobile-list-card");
        card.HasAttribute("aria-label").Should().BeFalse();
        card.TextContent.Should().Contain("Order 4711");
    }
}
