using MMCA.Common.Architecture.Tests.CancellationFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Verifies the <c>AsyncMethodsDeclareTrailingCancellationToken</c> fitness function against
/// deliberately-shaped fixture services: it flags a missing, misplaced or misnamed token, leaves a
/// compliant service (and a non-async or non-public member) alone, honors the per-method exemption list,
/// and never flags a signature the repo does not own.
/// </summary>
public sealed class CancellationTokenFitnessTests
{
    [Fact]
    public void Rule_FlagsMissingToken_ButNotCompliantOrNonAsyncMembers()
    {
        var message = RunRule();

        message.Should().Contain($"{nameof(MissingTokenFixtureService)}.RunAsync", "the method takes no token");
        message.Should().Contain(
            $"{nameof(MissingTokenFixtureService)}.PingAsync",
            "a parameterless async method is not excused: the token would simply be its only parameter");
        message.Should().NotContain(nameof(CompliantFixtureService), "every one of its async methods trails the token");
        message.Should().NotContain("HiddenAsync", "non-public methods are out of scope");
    }

    [Fact]
    public void Rule_FlagsMisplacedAndMisnamedTokens()
    {
        var message = RunRule();

        message.Should().Contain($"{nameof(MisplacedTokenFixtureService)}.RunAsync");
        message.Should().Contain("must be the LAST parameter");
        message.Should().Contain($"{nameof(MisnamedTokenFixtureService)}.RunAsync");
        message.Should().Contain("must be named 'cancellationToken'");
    }

    [Fact]
    public void Rule_HonorsExemptions_AndSignaturesTheRepoDoesNotOwn()
    {
        RunRule().Should().Contain(
            $"{nameof(ExemptableFixtureService)}.LegacyAsync",
            "without an exemption the offender is reported");

        var exempted = RunRule([$"{nameof(ExemptableFixtureService)}.LegacyAsync"]);

        exempted.Should().NotContain($"{nameof(ExemptableFixtureService)}.LegacyAsync");
        exempted.Should().NotContain(
            "MoveNextAsync",
            "an implicit implementation of an interface declared outside the map is auto-exempt: the signature is externally fixed");
    }

    private static string RunRule(IReadOnlyCollection<string>? exemptMethods = null)
    {
        var act = () => ArchitectureRules.AsyncMethodsDeclareTrailingCancellationToken(
            new CancellationTestMap(),
            exemptMethods);

        return act.Should().Throw<Exception>().Which.Message;
    }

    /// <summary>A map whose single Application layer is this test assembly, so the fixtures are in scope.</summary>
    private sealed class CancellationTestMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Application, typeof(CancellationTokenFitnessTests).Assembly)];
    }
}
