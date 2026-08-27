using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Self-test for the MudDataGrid sortable-column rule shipped in
/// <c>MMCA.Common.Testing.Architecture</c> (<see cref="SortableColumnConventionTestsBase"/>). The rule
/// scans markup TEXT, so the fixtures are real <c>.razor</c> files under <c>RazorFixtures</c>: an
/// <c>Offending</c> folder holding the defect in two spellings, and a <c>Clean</c> folder holding
/// every near-miss that breaks a naive text match (a sortable PropertyColumn, an unsortable
/// TemplateColumn, a bound value, a longer attribute name, a longer element name, a generic argument
/// carrying an angle bracket, and the whole defect commented out).
/// </summary>
public sealed class SortableColumnFitnessTests
{
    private static readonly string FixtureRoot = Path.Combine(
        ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"),
        "Tests",
        "Architecture",
        "MMCA.Common.Architecture.Tests",
        "RazorFixtures");

    private static string Offending => Path.Combine(FixtureRoot, "Offending");

    private static string Clean => Path.Combine(FixtureRoot, "Clean");

    [Fact]
    public void SortableTemplateColumn_IsFlaggedWithFileAndLine()
    {
        var act = () => ArchitectureRules.SortableGridColumnsUsePropertyColumn([Offending]);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("SortableTemplateColumnGrid.razor:7",
            "the report must name the file and the line the offending element starts on, even when the attribute sits on a later line");
    }

    [Fact]
    public void ExpressionBoundLiteralTrue_IsAlsoFlagged()
    {
        var act = () => ArchitectureRules.SortableGridColumnsUsePropertyColumn([Offending]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                "ExpressionSortableTemplateColumnGrid.razor:5",
                "Sortable=\"@(true)\" is the same defect written as a Razor expression");
    }

    [Fact]
    public void ConformingMarkup_Passes()
    {
        var act = () => ArchitectureRules.SortableGridColumnsUsePropertyColumn([Clean]);

        act.Should().NotThrow(
            "a sortable PropertyColumn, an unsortable or bound TemplateColumn, a longer attribute or element name, and commented-out markup are all conforming");
    }

    [Fact]
    public void MissingRoot_FailsRatherThanScanningNothing()
    {
        var missing = Path.Combine(FixtureRoot, "NoSuchFolder");

        var act = () => ArchitectureRules.SortableGridColumnsUsePropertyColumn([missing]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                "markup root not found",
                "a path typo must fail the gate instead of quietly making it vacuous");
    }
}
