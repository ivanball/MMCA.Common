using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.Localization;

/// <summary>
/// L243: the Spanish accent gate accepts unaccented <c>aun</c> ("even", as in "aun asi"), which is
/// correct Spanish, while still rejecting a word whose unaccented spelling is the mistake. The
/// subclasses are private, so xUnit does not discover them; their fact is invoked directly over the
/// <c>SpanishFixtures</c> folders.
/// </summary>
public sealed class SpanishAccentFitnessTests
{
    private static readonly string FixtureRoot = Path.Combine(
        ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"),
        "Tests",
        "Architecture",
        "MMCA.Common.Architecture.Tests",
        "SpanishFixtures");

    [Fact]
    public void UnaccentedAun_MeaningEven_IsAccepted()
    {
        var act = () => new FixtureSpanishTests(Path.Combine(FixtureRoot, "Compliant")).Spanish_resources_keep_their_accents();

        act.Should().NotThrow();
    }

    // L249: confirmo and visito are also correct unaccented first-person verb forms
    // ("Confirmo que acepto..."), so a consent label must not need an exception.
    [Fact]
    public void UnaccentedVerbForms_ThatAreCorrectSpanish_AreAccepted()
    {
        var act = () => new FixtureSpanishTests(Path.Combine(FixtureRoot, "CompliantVerbForms")).Spanish_resources_keep_their_accents();

        act.Should().NotThrow();
    }

    [Fact]
    public void AWordMissingItsAccent_IsStillRejected()
    {
        var act = () => new FixtureSpanishTests(Path.Combine(FixtureRoot, "Offending")).Spanish_resources_keep_their_accents();

        act.Should().Throw<XunitException>().Which.Message.Should().Contain("todavia");
    }

    private sealed class FixtureSpanishTests(string resourceRoot) : SpanishAccentTestsBase
    {
        protected override string ResourceRoot => resourceRoot;
    }
}
