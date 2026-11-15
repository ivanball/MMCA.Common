using MMCA.Common.Architecture.Tests.CommandValidatorFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.TestGaps;

/// <summary>
/// Self-test for the handler test-coverage rule (<see cref="HandlerTestCoverageTestsBase"/>). The map
/// points at THIS assembly, whose command-validator fixtures compile real <c>ICommandHandler</c>
/// types, and each fact writes a throwaway <c>Tests/</c> tree that names some of them. It pins: a
/// handler named in no test file is flagged, a whole-word mention covers it, a longer identifier that
/// merely contains the name does not, the allowlist exempts what it names and flags an entry that
/// names nothing, and a map with too few handlers fails as vacuous.
/// </summary>
public sealed class HandlerTestCoverageFitnessTests
{
    private readonly FixtureModuleMap _map = new();

    [Fact]
    public void HandlerNamedInNoTestFile_IsFlagged()
    {
        using var repo = new TempRepo().Write("Tests/Unrelated/SomethingTests.cs", "public class SomethingTests { }");

        var act = () => ArchitectureRules.HandlersHaveTests(_map, repo.Root, ["Tests"], []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(ArchiveTicketHandler),
                "a handler that no test file names has no test");
    }

    [Fact]
    public void HandlerNamedAsAWholeWord_IsCovered()
    {
        using var repo = new TempRepo().Write(
            "Tests/Tickets/CreateTicketHandlerTests.cs",
            "var sut = new CreateTicketHandler();");

        var act = () => ArchitectureRules.HandlersHaveTests(_map, repo.Root, ["Tests"], []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{typeof(ArchiveTicketHandler).FullName} has no test",
                "the rule ran and still reports the handlers nothing names")
            .And.NotContain(
                $"{typeof(CreateTicketHandler).FullName} ",
                "a test that constructs the handler covers it");
    }

    [Fact]
    public void LongerIdentifierContainingTheName_DoesNotCount()
    {
        using var repo = new TempRepo().Write(
            "Tests/Tickets/Lookalike.cs",
            "var x = new PreArchiveTicketHandlerProbe();");

        var act = () => ArchitectureRules.HandlersHaveTests(_map, repo.Root, ["Tests"], []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                typeof(ArchiveTicketHandler).FullName!,
                "only a whole-word mention names the handler; a longer identifier is a different type");
    }

    [Fact]
    public void AllowlistedHandler_IsNotFlagged()
    {
        using var repo = new TempRepo().Write("Tests/Unrelated/SomethingTests.cs", "class X { }");
        var allowed = typeof(ArchiveTicketHandler).FullName!;

        var act = () => ArchitectureRules.HandlersHaveTests(_map, repo.Root, ["Tests"], [allowed]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{typeof(PurgeTicketsHandler).FullName} has no test",
                "the rule ran and still reports the handlers the allowlist does not name")
            .And.NotContain(
                $"{allowed} has no test",
                "an allowlisted handler is exempt by name");
    }

    [Fact]
    public void AllowlistEntryNamingNoHandler_IsFlaggedAsStale()
    {
        using var repo = new TempRepo().Write("Tests/Unrelated/SomethingTests.cs", "class X { }");
        const string stale = "MMCA.Common.Architecture.Tests.Nowhere.GhostHandler";

        var act = () => ArchitectureRules.HandlersHaveTests(_map, repo.Root, ["Tests"], [stale]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                stale,
                "an allowlist entry that names no handler is stale and must be removed, or the list only grows");
    }

    [Fact]
    public void HandlerInventory_CountsTheFixtureHandlers() =>
        ArchitectureRules.HandlerCount(_map).Should().BeGreaterThanOrEqualTo(
            5,
            "the command-validator fixtures compile at least five ICommandHandler types into this assembly");

    [Fact]
    public void BaseWithTooFewHandlers_FailsAsVacuous()
    {
        var sut = new VacuousHandlerCoverage();

        var act = sut.HandlerInventory_ShouldNotBe_Empty;

        act.Should().Throw<XunitException>("a minimum above the handlers the map resolves means the gate reads nothing it should");
    }

    private sealed class VacuousHandlerCoverage : HandlerTestCoverageTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureModuleMap();

        protected override string RepoRoot => Path.GetTempPath();

        protected override int MinimumHandlers => 100_000;
    }

    private sealed class FixtureModuleMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Tickets", Layer.Application, typeof(CreateTicketCommand).Assembly),
        ];
    }
}
