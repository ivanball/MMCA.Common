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
/// inside the guard's argument. A <c>Composed</c> folder holds one composed service host
/// (<c>AddMmcaServiceHost</c>) on the default authentication with its near-misses, and an <c>Issuer</c>
/// folder holds the token issuer selected inside the call and through an options variable. MMCA.Common
/// hosts no service, so the base itself runs in the consumers.
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

    private static string Composed => Path.Combine(FixtureRoot, "Composed");

    private static string Issuer => Path.Combine(FixtureRoot, "Issuer");

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
    public void ComposedHost_CountsTowardTheFloor_AndNearMissesAreNotCounted()
    {
        var conforming = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Composed], minimumCalls: 1);
        var overCounted = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Composed], minimumCalls: 2);

        conforming.Should().NotThrow("a composed host on the default authentication validates forwarded tokens fail-closed by construction");
        overCounted.Should().Throw<XunitException>(
            "the comment, the documentation mention and the declaration are not calls, so the composed fixture holds exactly one");
    }

    [Fact]
    public void ComposedTokenIssuer_IsNotCounted()
    {
        var oneHost = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Composed, Issuer], minimumCalls: 1);
        var twoHosts = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Composed, Issuer], minimumCalls: 2);

        oneHost.Should().NotThrow("the forwarded composed host counts");
        twoHosts.Should().Throw<XunitException>(
            "the issuer selects TokenIssuer, in the call's options or in a variable passed to it, so neither issuer file counts");
    }

    [Fact]
    public void DirectAndComposedHosts_TogetherSatisfyTheFloor()
    {
        var mixed = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Clean, Composed, Issuer], minimumCalls: 2);
        var overCounted = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Clean, Composed, Issuer], minimumCalls: 3);

        mixed.Should().NotThrow("one direct call and one composed call are two forwarded-JWT hosts");
        overCounted.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("found 2 forwarded-JWT host(s) (1 AddForwardedJwtBearer call(s), 1 AddMmcaServiceHost call(s) that are not the token issuer), expected at least 3");
    }

    [Fact]
    public void FallbackInDirectCall_IsStillFlagged_WhenComposedHostsArePresent()
    {
        var act = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Offending, Composed], minimumCalls: 1);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("FallbackProgram.cs:4 passes an audience not resolved through JwtAudience.RequireConfigured")
            .And.Contain("MaskedProgram.cs:5 carries a ?? fallback");
    }

    [Fact]
    public void NoForwardedHostOfEitherShape_FailsTheFloor()
    {
        var act = () => ArchitectureRules.ForwardedJwtBearerAudienceIsFailClosed([Issuer], minimumCalls: 1);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("found 0 forwarded-JWT host(s)");
    }

    [Fact]
    public void RuleLiteral_MatchesTheShippedGuard() =>
        nameof(JwtAudience.RequireConfigured).Should().Be(
            "RequireConfigured",
            "the rule matches 'JwtAudience.RequireConfigured(' by text, so renaming the guard must move the rule with it");
}
