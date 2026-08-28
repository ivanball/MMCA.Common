using Bunit;
using Microsoft.AspNetCore.Components;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Reads the shared <c>ErrorSummary</c> component out of a rendered component under test, so a form
/// test can assert on the validation messages a user would see rather than on raw markup.
/// </summary>
public static class ErrorSummaryExtensions
{
    /// <summary>The class the summary's title carries, and the marker that identifies its alert.</summary>
    public const string ErrorSummaryTitleClass = "mmca-error-summary-title";

    extension<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
    {
        /// <summary>
        /// The messages the shared <c>ErrorSummary</c> is currently showing, one entry per broken rule,
        /// or an empty list when no summary is rendered.
        /// </summary>
        /// <remarks>
        /// The component renders SEVERAL messages as a <c>&lt;ul&gt;</c> but a SINGLE one as plain text
        /// inside the alert, so a test that only queried <c>li</c> would read an empty summary exactly
        /// when one rule is broken. This reads both shapes and strips the alert's title.
        /// </remarks>
        public IReadOnlyList<string> ErrorSummaryMessages()
        {
            ArgumentNullException.ThrowIfNull(cut);

            var alert = cut.FindAll(".mud-alert")
                .FirstOrDefault(element => element.QuerySelector("." + ErrorSummaryTitleClass) is not null);
            if (alert is null)
            {
                return [];
            }

            var items = alert.QuerySelectorAll("li");
            if (items.Length > 0)
            {
                return [.. items.Select(item => item.TextContent.Trim())];
            }

            var title = alert.QuerySelector("." + ErrorSummaryTitleClass)?.TextContent ?? string.Empty;
            var single = alert.TextContent.Replace(title, string.Empty, StringComparison.Ordinal).Trim();
            return single.Length == 0 ? [] : [single];
        }
    }
}
