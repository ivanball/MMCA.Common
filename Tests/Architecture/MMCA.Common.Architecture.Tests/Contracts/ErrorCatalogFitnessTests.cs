using MMCA.Common.Architecture.Tests.ErrorCodeFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Contracts;

/// <summary>
/// Self-test for the error-catalog rules shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="ErrorCatalogTestsBase"/>). The rules read codes out of IL, so this points a map at THIS
/// assembly, whose <c>ErrorCodeFixtures</c> compile the catalog they must judge, and pins each
/// behaviour: a cross-type collision fails, one code reused across two branches of a single type does
/// not, an unprefixed code fails, an allowlisted shared code is exempt, and a code built at run time
/// is reported as UNVERIFIABLE rather than passed or failed.
/// </summary>
public sealed class ErrorCatalogFitnessTests
{
    private static readonly string[] SharedCodes = ["Error.NotFound"];

    private readonly FixtureModuleMap _map = new();

    [Fact]
    public void DuplicateCode_AcrossTwoTypes_IsFlagged()
    {
        var act = () => ArchitectureRules.ErrorCodesAreUnique(_map, SharedCodes);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("Tickets.NotFound", "the same code is constructed by two different types");
        message.Should().Contain(nameof(TicketErrors));
        message.Should().Contain(nameof(DuplicateTicketErrors));
    }

    [Fact]
    public void CodeReusedAcrossBranchesOfOneType_IsNotADuplicate()
    {
        var act = () => ArchitectureRules.ErrorCodesAreUnique(_map, SharedCodes);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                "Tickets.Invalid",
                "one error with two exits in the same type is not a catalog collision");
    }

    [Fact]
    public void SharedCode_OnTheAllowList_IsExemptFromUniqueness()
    {
        var act = () => ArchitectureRules.ErrorCodesAreUnique(_map, SharedCodes);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(SharedCodeErrors),
                "the generic statics on Error exist to be reused, which is what the allow list records");
    }

    [Fact]
    public void UnprefixedCode_IsFlagged()
    {
        var act = () => ArchitectureRules.ErrorCodesUseAnAllowedPrefix(_map, IsTicketsCode, SharedCodes);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("SomethingBroke", "a code with no module prefix must be reported");
        message.Should().Contain(nameof(UnprefixedErrors), "the report must name the owning type");
    }

    [Fact]
    public void PrefixedCodes_AndAllowedSharedCodes_PassThePrefixRule()
    {
        var act = () => ArchitectureRules.ErrorCodesUseAnAllowedPrefix(_map, IsTicketsCode, SharedCodes);

        var message = act.Should().Throw<XunitException>(
            "the unprefixed fixture keeps the rule failing").Which.Message;

        message.Should().NotContain("Tickets.AlreadyClosed", "a correctly prefixed code must pass");
        message.Should().NotContain("Error.NotFound", "an allowlisted shared code is exempt from the prefix rule");
    }

    [Fact]
    public void DynamicCode_IsReportedAsUnverifiable()
    {
        var act = () => ArchitectureRules.ErrorCodesUseAnAllowedPrefix(_map, IsTicketsCode, SharedCodes);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("UNVERIFIABLE", "a code the scan cannot read is neither passed nor failed");
        message.Should().Contain(nameof(DynamicErrors), "the blind spot must name the method that owns it");
    }

    [Fact]
    public void DistinctCodeCount_CountsTheLiteralCatalog()
    {
        var count = ArchitectureRules.DistinctErrorCodeCount(_map);

        count.Should().BeGreaterThanOrEqualTo(
            5,
            "the fixtures compile Tickets.NotFound, Tickets.AlreadyClosed, Tickets.Invalid, SomethingBroke and Error.NotFound as literals");
    }

    /// <summary>The fixture catalog's prefix convention, standing in for a consumer's module names.</summary>
    private static bool IsTicketsCode(string code) => code.StartsWith("Tickets.", StringComparison.Ordinal);

    /// <summary>
    /// A map registering this test assembly as a MODULE Application layer: the catalog rules read the
    /// per-module Domain and Application assemblies, deliberately skipping framework layers.
    /// </summary>
    private sealed class FixtureModuleMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Tickets", Layer.Application, typeof(TicketErrors).Assembly),
        ];
    }
}
