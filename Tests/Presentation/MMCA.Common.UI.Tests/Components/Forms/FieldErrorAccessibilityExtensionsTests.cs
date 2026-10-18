using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// Covers the shipped <c>ShouldExposeFieldErrorAccessibly()</c> assertion in
/// <c>MMCA.Common.Testing.UI</c> against a tiny probe component that renders an invalid field and its
/// error text, with each of the three accessibility facts switchable: a compliant render passes, and
/// dropping the alert role, the <c>aria-invalid</c> marker or the <c>aria-describedby</c> link each
/// fails with a message naming what is missing.
/// </summary>
public sealed class FieldErrorAccessibilityExtensionsTests : BunitTestBase
{
    private const string InputId = "email";
    private const string Message = "Email is required.";

    [Fact]
    public void CompliantField_Passes()
    {
        var cut = RenderUnderTest<FieldErrorProbe>(_ => { });

        FluentActions.Invoking(() => cut.ShouldExposeFieldErrorAccessibly(InputId, Message)).Should().NotThrow();
    }

    [Fact]
    public void MissingAlertRole_Fails()
    {
        var cut = RenderUnderTest<FieldErrorProbe>(p => p.Add(c => c.RenderAlertRole, false));

        FluentActions.Invoking(() => cut.ShouldExposeFieldErrorAccessibly(InputId, Message))
            .Should().Throw<Exception>().Which.Message.Should().Contain("role=\"alert\"");
    }

    [Fact]
    public void MissingAriaInvalid_Fails()
    {
        var cut = RenderUnderTest<FieldErrorProbe>(p => p.Add(c => c.MarkInvalid, false));

        FluentActions.Invoking(() => cut.ShouldExposeFieldErrorAccessibly(InputId, Message))
            .Should().Throw<Exception>().Which.Message.Should().Contain("aria-invalid");
    }

    [Fact]
    public void MissingAriaDescribedBy_Fails()
    {
        var cut = RenderUnderTest<FieldErrorProbe>(p => p.Add(c => c.DescribeByError, false));

        FluentActions.Invoking(() => cut.ShouldExposeFieldErrorAccessibly(InputId, Message))
            .Should().Throw<Exception>().Which.Message.Should().Contain("aria-describedby");
    }

    [Fact]
    public void DescribedByTextWithoutTheMessage_Fails()
    {
        var cut = RenderUnderTest<FieldErrorProbe>(_ => { });

        FluentActions.Invoking(() => cut.ShouldExposeFieldErrorAccessibly(InputId, "Email must be valid."))
            .Should().Throw<Exception>().Which.Message.Should().Contain("Email must be valid.");
    }

    /// <summary>An input and its error text, with each accessibility fact switchable by a parameter.</summary>
    private sealed class FieldErrorProbe : ComponentBase
    {
        [Parameter]
        public bool RenderAlertRole { get; set; } = true;

        [Parameter]
        public bool MarkInvalid { get; set; } = true;

        [Parameter]
        public bool DescribeByError { get; set; } = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "id", InputId);
            if (MarkInvalid)
            {
                builder.AddAttribute(2, "aria-invalid", "true");
            }

            if (DescribeByError)
            {
                builder.AddAttribute(3, "aria-describedby", InputId + "-error");
            }

            builder.CloseElement();

            builder.OpenElement(4, "div");
            builder.AddAttribute(5, "id", InputId + "-error");
            if (RenderAlertRole)
            {
                builder.AddAttribute(6, "role", "alert");
            }

            builder.AddContent(7, Message);
            builder.CloseElement();
        }
    }
}
