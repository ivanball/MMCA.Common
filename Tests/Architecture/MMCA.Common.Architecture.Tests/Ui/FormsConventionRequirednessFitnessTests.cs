using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// M258: the create-form requiredness fact strips Razor and HTML comments before it looks for
/// <c>ModelValidation.IsRequired</c>, so a commented-out marker no longer satisfies it. The fixtures
/// live under <c>RazorFixtures/Forms</c>; the subclasses are private, so xUnit does not discover them
/// and an offending one does not fail the run; their fact is invoked directly.
/// </summary>
public sealed class FormsConventionRequirednessFitnessTests
{
    private static readonly string FormsFixtureRoot = Path.Combine(
        ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"),
        "Tests",
        "Architecture",
        "MMCA.Common.Architecture.Tests",
        "RazorFixtures",
        "Forms");

    [Fact]
    public void ACommentedOutRequirednessMarker_DoesNotSatisfyTheGate()
    {
        var act = () => new FixtureFormsTests(Path.Combine(FormsFixtureRoot, "Commented")).AdminCreateForms_ReadRequirednessOffTheirModel();

        act.Should().Throw<XunitException>().Which.Message.Should().Contain("WidgetCreate.razor");
    }

    [Fact]
    public void ALiveMarkerInTheSiblingFieldComponent_SatisfiesTheGate()
    {
        var act = () => new FixtureFormsTests(Path.Combine(FormsFixtureRoot, "Clean")).AdminCreateForms_ReadRequirednessOffTheirModel();

        act.Should().NotThrow();
    }

    private sealed class FixtureFormsTests(string modulesRoot) : FormsConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

        protected override string ModulesRoot => modulesRoot;
    }
}
