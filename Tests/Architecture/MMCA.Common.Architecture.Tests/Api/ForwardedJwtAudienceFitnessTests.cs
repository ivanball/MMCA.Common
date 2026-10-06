using MMCA.Common.API.Startup.Auth;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Self-test for the fail-closed audience rule behind <see cref="ForwardedJwtAudienceTestsBase"/>. The
/// rule scans <c>.cs</c> TEXT, so the fixtures are uncompiled files under <c>JwtAudienceFixtures</c>: a
/// <c>Clean</c> folder holding the conforming call plus the near-misses a naive match trips on (a
/// commented-out call, a documentation mention and the extension's own declaration), and an
/// <c>Offending</c> folder holding a <c>??</c> fallback in place of the guard and a fallback masked
/// inside the guard's argument. MMCA.Common hosts no service, so the base itself runs in the consumers.
/// </summary>
public sealed class ForwardedJwtAudienceFitnessTests
{
    private static readonly string FixtureRoot = Path.Combine(
        ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"),
        "Tests",
        "Architecture",
        "MMCA.Common.Architecture.Tests",
        "JwtAudienceFixtures");

    private static string Clean => Path.Combine(FixtureRoot, "Clean");

    private static string Offending => Path.Combine(FixtureRoot, "Offending");

    [Fact]
    public void ConformingCall_Passes_AndNearMissesAreNotCounted()
    {
        var conforming = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Clean], minimumCalls: 1);
        var overCounted = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Clean], minimumCalls: 2);

        conforming.Should().NotThrow("the guarded call is the convention");
        overCounted.Should().Throw<XunitException>(
            "the comment, the documentation mention and the declaration are not calls, so the clean fixture holds exactly one");
    }

    [Fact]
    public void FallbackInsteadOfTheGuard_IsFlaggedWithFileAndLine()
    {
        var act = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Offending], minimumCalls: 1);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("FallbackProgram.cs:4 passes an audience not resolved through JwtAudience.RequireConfigured");
    }

    [Fact]
    public void FallbackMaskedInsideTheGuard_IsFlagged()
    {
        var act = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Offending], minimumCalls: 1);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("MaskedProgram.cs:5 carries a ?? fallback");
    }

    [Fact]
    public void MissingRoot_FailsRatherThanScanningNothing()
    {
        var act = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Path.Combine(FixtureRoot, "NoSuchFolder")], minimumCalls: 0);

        act.Should().Throw<XunitException>().Which.Message.Should().Contain("source root not found");
    }

    [Fact]
    public void RuleLiteral_MatchesTheShippedGuard() =>
        nameof(JwtAudience.RequireConfigured).Should().Be(
            "RequireConfigured",
            "the rule matches 'JwtAudience.RequireConfigured(' by text, so renaming the guard must move the rule with it");
}
