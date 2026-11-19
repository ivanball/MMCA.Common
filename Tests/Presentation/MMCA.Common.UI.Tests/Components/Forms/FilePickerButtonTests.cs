using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MMCA.Common.UI.Components.Forms;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// bUnit tests for <see cref="FilePickerButton"/>: the file input keeps its tab stop (visually
/// hidden, never the <c>hidden</c> attribute), the label-shaped button points at it, and the
/// caller's id, accept list, disabled state and extra attributes reach the input.
/// </summary>
public sealed class FilePickerButtonTests : BunitTestBase
{
    [Fact]
    public void Input_IsVisuallyHidden_NotRemovedFromTheTabOrder()
    {
        var cut = RenderPicker();

        var input = cut.Find("input[type=file]");
        input.HasAttribute("hidden").Should().BeFalse("the hidden attribute drops the input out of the tab order (WCAG 2.1.1)");
        input.ClassList.Should().Contain("visually-hidden");
        input.HasAttribute("tabindex").Should().BeFalse();
    }

    [Fact]
    public void Label_PointsAtTheInput_AndCarriesTheText()
    {
        var cut = RenderPicker();

        var input = cut.Find("input[type=file]");
        var label = cut.Find("label");
        label.GetAttribute("for").Should().Be(input.Id);
        label.TextContent.Should().Contain("Upload slides");
        input.Id.Should().StartWith("file-picker-");
    }

    [Fact]
    public void InputId_WhenSet_IsUsedAsIs()
    {
        var cut = RenderPicker(p => p.Add(c => c.InputId, "avatar-file-input"));

        cut.Find("input[type=file]").Id.Should().Be("avatar-file-input");
        cut.Find("label").GetAttribute("for").Should().Be("avatar-file-input");
    }

    [Fact]
    public void Accept_Disabled_Multiple_AndExtraAttributes_ReachTheInput()
    {
        var cut = RenderPicker(p => p
            .Add(c => c.Accept, ".csv,text/csv")
            .Add(c => c.Disabled, true)
            .Add(c => c.Multiple, true)
            .AddUnmatched("data-testid", "roster-file"));

        var input = cut.Find("input[type=file]");
        input.GetAttribute("accept").Should().Be(".csv,text/csv");
        input.HasAttribute("disabled").Should().BeTrue();
        input.HasAttribute("multiple").Should().BeTrue();
        input.GetAttribute("data-testid").Should().Be("roster-file");
    }

    [Fact]
    public void Multiple_AndDisabled_AreOmittedByDefault()
    {
        var input = RenderPicker().Find("input[type=file]");

        input.HasAttribute("multiple").Should().BeFalse();
        input.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void OnChange_IsWiredToTheInputFile()
    {
        var cut = RenderPicker();

        cut.FindComponent<InputFile>().Instance.OnChange.HasDelegate.Should().BeTrue();
    }

    private IRenderedComponent<FilePickerButton> RenderPicker(
        Action<ComponentParameterCollectionBuilder<FilePickerButton>>? configure = null) =>
        RenderUnderTest<FilePickerButton>(p =>
        {
            p.Add(c => c.Text, "Upload slides")
             .Add(c => c.OnChange, EventCallback.Factory.Create<InputFileChangeEventArgs>(this, _ => { }));
            configure?.Invoke(p);
        });
}
