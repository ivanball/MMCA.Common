using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Asserts that a rendered component exposes a field's validation error to assistive technology, not
/// just to sighted users: the error is announced, the field is marked invalid, and the field points
/// at the text that explains what is wrong (WCAG 2.1 SC 3.3.1 Error Identification and SC 4.1.2).
/// </summary>
public static class FieldErrorAccessibilityExtensions
{
    extension<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
    {
        /// <summary>
        /// Asserts the three facts a screen-reader user depends on when the field with id
        /// <paramref name="inputId"/> is invalid: an element with <c>role="alert"</c> is rendered, the
        /// field carries <c>aria-invalid="true"</c>, and one of the ids in its
        /// <c>aria-describedby</c> names an element whose text contains
        /// <paramref name="expectedMessage"/>.
        /// </summary>
        /// <param name="inputId">The <c>id</c> attribute of the input under test.</param>
        /// <param name="expectedMessage">The validation message the field must be described by.</param>
        public void ShouldExposeFieldErrorAccessibly(string inputId, string expectedMessage)
        {
            ArgumentNullException.ThrowIfNull(cut);
            ArgumentException.ThrowIfNullOrWhiteSpace(inputId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedMessage);

            cut.FindAll("[role=\"alert\"]").Should().NotBeEmpty(
                because: "a validation error must be announced through an element with role=\"alert\"");

            var input = cut.FindAll($"[id=\"{inputId}\"]").Should().ContainSingle(
                because: $"exactly one element must carry id=\"{inputId}\"").Subject;

            input.GetAttribute("aria-invalid").Should().Be(
                "true",
                because: $"the field #{inputId} must carry aria-invalid=\"true\" while it is invalid");

            var describedBy = (input.GetAttribute("aria-describedby") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            describedBy.Should().NotBeEmpty(
                because: $"the field #{inputId} must reference its error text through aria-describedby");

            var descriptions = describedBy
                .SelectMany(id => cut.FindAll($"[id=\"{id}\"]"))
                .Select(element => element.TextContent)
                .ToList();
            descriptions.Should().NotBeEmpty(
                because: $"aria-describedby on #{inputId} ('{string.Join(' ', describedBy)}') must reference an element that exists");
            descriptions.Should().Contain(
                text => text.Contains(expectedMessage, StringComparison.Ordinal),
                because: $"an element referenced by aria-describedby on #{inputId} must contain the message '{expectedMessage}'");
        }
    }
}
