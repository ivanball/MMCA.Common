using MMCA.Common.Architecture.Tests.ClockReadFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Runs the clock-read rule (<see cref="ClockReadTestsBase"/>) over MMCA.Common's own Domain and
/// Application through the inherited fact, and self-tests the rule against the compiled shapes in
/// <c>ClockReadFixtures</c>: a rule that reads IL cannot be verified by reading it. The fixture map
/// registers this whole test assembly, so the self-tests assert on what the report names rather than
/// on the report being otherwise empty.
/// </summary>
public sealed class ClockReadTests : ClockReadTestsBase
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.ClockReadFixtures";

    private readonly FixtureAssemblyMap _fixtureMap = new();

    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    [Fact]
    public void DirectReadsOfEitherClockType_AreFlagged()
    {
        var report = Report([]);

        report.Should().Contain($"{nameof(UtcNowReadingFixture)}.Stamp reads DateTime.UtcNow");
        report.Should().Contain($"{nameof(OffsetNowReadingFixture)}.Stamp reads DateTimeOffset.Now");
    }

    [Fact]
    public void ReadsInsideLambdaAndAsyncBodies_AreFlaggedUnderTheirSourceMember()
    {
        var report = Report([]);

        report.Should().Contain(
            $"{nameof(LambdaClockReadingFixture)}.Clock reads DateTime.UtcNow",
            "a lambda body lives in a compiler-generated nested type and must still be attributed to the member that wrote it");
        report.Should().Contain(
            $"{nameof(AsyncClockReadingFixture)}.StampAsync reads DateTimeOffset.UtcNow",
            "an async body lives in a state machine and must still be attributed to the member that wrote it");
    }

    [Fact]
    public void InjectedTimeProvider_IsNotFlagged() =>
        Report([]).Should().NotContain(
            nameof(InjectedClockFixture),
            "an injected TimeProvider is exactly the shape the rule asks for");

    [Fact]
    public void MemberAllowlistEntry_ExemptsOnlyThatMember()
    {
        var report = Report([$"{FixtureNamespace}.{nameof(TwoMemberClockFixture)}.Exempted"]);

        report.Should().NotContain($"{nameof(TwoMemberClockFixture)}.Exempted reads");
        report.Should().Contain($"{nameof(TwoMemberClockFixture)}.StillReported reads DateTime.UtcNow");
    }

    [Fact]
    public void NamespaceAllowlistEntry_ExemptsEveryTypeUnderIt() =>
        Report([FixtureNamespace]).Should().NotContain(FixtureNamespace);

    /// <summary>The rule's failure message over the fixture map, or empty when it passes.</summary>
    private string Report(IReadOnlyCollection<string> allowed)
    {
        try
        {
            ArchitectureRules.DomainAndApplicationDoNotReadTheClock(_fixtureMap, allowed);
            return string.Empty;
        }
        catch (XunitException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>A map whose Domain layer is this test assembly, so the rule scans the fixtures.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Domain, typeof(InjectedClockFixture).Assembly),
        ];
    }
}
