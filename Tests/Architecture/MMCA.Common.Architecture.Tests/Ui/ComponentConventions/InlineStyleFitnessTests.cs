using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Ui.ComponentConventions;

/// <summary>
/// Self-test for <see cref="ArchitectureRules.RazorMarkupCarriesNoInlineStyles"/>, the rule behind
/// <see cref="InlineStyleTestsBase"/>. The detector cases are lifted unchanged from ADC's local
/// <c>InlineStyleTests</c> regex; the fixtures under <c>RazorFixtures/InlineStyles</c> hold every banned
/// spelling (<c>Offending</c>) and the near-misses a naive match trips on (<c>Clean</c>).
/// </summary>
public sealed class InlineStyleFitnessTests
{
    private static readonly string FixtureRoot = Path.Combine(
        ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"),
        "Tests",
        "Architecture",
        "MMCA.Common.Architecture.Tests",
        "RazorFixtures",
        "InlineStyles");

    [Theory]
    [InlineData("<div style=\"gap: 1rem;\">", "style=")]
    [InlineData("<MudText Style=\"word-break: break-all;\">", "Style=")]
    [InlineData("<TemplateColumn CellStyle=\"width: 110px;\" />", "CellStyle=")]
    [InlineData("<TemplateColumn HeaderStyle=\"width: 110px;\" />", "HeaderStyle=")]
    [InlineData("<MudDataGrid RowStyleFunc=\"@RowStyle\" />", "RowStyleFunc=")]
    [InlineData("<MudCard Style = \"@CardStyle(n)\">", "Style=")]
    public void Detector_FlagsEveryInlineStyleSpelling(string line, string attribute) =>
        ArchitectureRules.InlineStyleViolations("Sample.razor", [line])
            .Should().ContainSingle().Which.Should().Be($"Sample.razor:1 {attribute}");

    [Theory]
    [InlineData("<div class=\"mmca-auth-actions\">")]
    [InlineData("<div data-style=\"compact\">")]
    [InlineData("<ul class=\"list-style-none\">")]
    [InlineData("<TemplateColumn CellClass=\"mmca-actions-cell\" />")]
    public void Detector_LeavesNearMissesAlone(string line) =>
        ArchitectureRules.InlineStyleViolations("Sample.razor", [line]).Should().BeEmpty();

    [Fact]
    public void OffendingMarkup_IsFlaggedWithFileLineAndAttribute()
    {
        var act = () => ArchitectureRules.RazorMarkupCarriesNoInlineStyles([Path.Combine(FixtureRoot, "Offending")], minimumRazorFiles: 1);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("InlineStyled.razor:2 style=");
        message.Should().Contain("InlineStyled.razor:3 Style=");
        message.Should().Contain("InlineStyled.razor:4 CellStyle=");
        message.Should().Contain("InlineStyled.razor:5 RowStyleFunc=");
    }

    [Fact]
    public void ConformingMarkup_Passes()
    {
        var act = () => ArchitectureRules.RazorMarkupCarriesNoInlineStyles([Path.Combine(FixtureRoot, "Clean")], minimumRazorFiles: 1);

        act.Should().NotThrow("classes, data-style and list-style CSS text are not inline styles");
    }

    [Fact]
    public void AllowListEntry_ExemptsOnlyThePrefixItNames()
    {
        var exemptFile = () => ArchitectureRules.RazorMarkupCarriesNoInlineStyles(
            [Path.Combine(FixtureRoot, "Offending")], minimumRazorFiles: 1, allowedViolations: ["InlineStyled.razor"]);
        var exemptOneLine = () => ArchitectureRules.RazorMarkupCarriesNoInlineStyles(
            [Path.Combine(FixtureRoot, "Offending")], minimumRazorFiles: 1, allowedViolations: ["InlineStyled.razor:2 "]);

        exemptFile.Should().NotThrow("a file-level entry exempts every line of that file");
        exemptOneLine.Should().Throw<XunitException>("a line-level entry leaves the other lines flagged");
    }

    [Fact]
    public void TooFewFiles_FailsRatherThanPassingVacuously()
    {
        var act = () => ArchitectureRules.RazorMarkupCarriesNoInlineStyles([Path.Combine(FixtureRoot, "Clean")], minimumRazorFiles: 2);

        act.Should().Throw<XunitException>().Which.Message.Should().Contain("scanned 1 .razor file(s), expected at least 2");
    }

    [Fact]
    public void MissingRoot_FailsRatherThanScanningNothing()
    {
        var act = () => ArchitectureRules.RazorMarkupCarriesNoInlineStyles([Path.Combine(FixtureRoot, "NoSuchFolder")], minimumRazorFiles: 0);

        act.Should().Throw<XunitException>().Which.Message.Should().Contain("markup root not found");
    }
}
