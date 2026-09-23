using MMCA.Common.Architecture.Tests.ReadRepositoryFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Cqrs.Repositories;

/// <summary>
/// Runs the read-repository rule (<see cref="QueryHandlerReadRepositoryTestsBase"/>) over MMCA.Common's
/// own Application layer through the inherited fact, and self-tests the rule against the compiled
/// handlers in <c>ReadRepositoryFixtures</c>: a rule that reads IL cannot be verified by reading it.
/// The fixture map registers this whole test assembly, so the self-tests assert on what the report
/// names rather than on the report being otherwise empty.
/// </summary>
public sealed class QueryHandlerReadRepositoryTests : QueryHandlerReadRepositoryTestsBase
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.ReadRepositoryFixtures";

    private readonly FixtureAssemblyMap _fixtureMap = new();

    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    [Fact]
    public void QueryHandlerAskingForTheWriteRepository_IsFlagged_EvenInsideAnAsyncBody() =>
        Report([]).Should().Contain(
            nameof(WriteRepositoryQueryHandlerFixture),
            "the GetRepository call sits after an await, inside the compiler's state machine, and must still be found");

    [Fact]
    public void QueryHandlerAskingForTheReadRepository_IsNotFlagged() =>
        Report([]).Should().NotContain(nameof(ReadRepositoryQueryHandlerFixture));

    [Fact]
    public void CommandHandlerAskingForTheWriteRepository_IsOutOfScope() =>
        Report([]).Should().NotContain(
            nameof(WriteRepositoryCommandHandlerFixture),
            "a command handler writes, so the write repository is the right one there");

    [Fact]
    public void AllowlistedHandler_IsSilenced() =>
        Report([$"{FixtureNamespace}.{nameof(WriteRepositoryQueryHandlerFixture)}"])
            .Should().NotContain(nameof(WriteRepositoryQueryHandlerFixture));

    /// <summary>The rule's failure message over the fixture map, or empty when it passes.</summary>
    private string Report(IReadOnlyCollection<string> allowed)
    {
        try
        {
            ArchitectureRules.QueryHandlersUseReadRepositories(_fixtureMap, allowed);
            return string.Empty;
        }
        catch (XunitException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>A map whose Application layer is this test assembly, so the rule scans the fixtures.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Application, typeof(ReadRepositoryQueryHandlerFixture).Assembly),
        ];
    }
}
