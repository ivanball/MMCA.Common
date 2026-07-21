using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Thin, intention-revealing helpers over bUnit's element API so component tests express user
/// actions (click a labelled button, read rendered text) instead of hand-rolling DOM queries.
/// Prefers accessible text over brittle CSS-path selectors.
/// </summary>
public static class BunitInteractionExtensions
{
    extension<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
    {
        /// <summary>Finds the first <c>&lt;button&gt;</c> whose visible text contains <paramref name="text"/> (case-insensitive). Throws with the available button texts if none match.</summary>
        public IElement FindButtonByText(string text)
        {
            var buttons = cut.FindAll("button");
            return buttons.FirstOrDefault(b => b.TextContent.Contains(text, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"No <button> containing text '{text}' was found. Buttons present: " +
                    $"[{string.Join(" | ", buttons.Select(b => b.TextContent.Trim()))}]");
        }

        /// <summary>Clicks the first <c>&lt;button&gt;</c> whose visible text contains <paramref name="text"/>.</summary>
        public void ClickButtonByText(string text)
            => cut.FindButtonByText(text).Click();

        /// <summary>True if the component's current markup contains <paramref name="text"/> (case-insensitive).</summary>
        public bool HasText(string text)
            => cut.Markup.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
