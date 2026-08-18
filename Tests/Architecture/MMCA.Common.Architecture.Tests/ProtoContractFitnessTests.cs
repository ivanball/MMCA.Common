using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Verifies the <c>ProtoContractsMatchFrozenList</c> fitness function against fixture <c>.proto</c>
/// files: the pinned file matches its frozen snapshot, and the drifted copy (one renumbered field,
/// one added rpc) is reported with both changes named.
/// <para>
/// MMCA.Common ships no protos of its own, so this is the framework's only exercise of the rule; the
/// gate itself is consumer-facing and consumers subclass <c>ProtoContractTestsBase</c>.
/// </para>
/// </summary>
public sealed class ProtoContractFitnessTests
{
    private const string SolutionFileName = "MMCA.Common.slnx";

    private const string TestDataDirectory = "Tests/Architecture/MMCA.Common.Architecture.Tests/TestData";

    private static readonly string[] PinnedProto = [$"{TestDataDirectory}/fitness-catalog.proto"];

    private static readonly string[] DriftedProto = [$"{TestDataDirectory}/fitness-catalog-drifted.proto"];

    /// <summary>
    /// The committed snapshot of <c>fitness-catalog.proto</c>: services with their rpcs (streaming
    /// flags included), message fields with their labels, types and NUMBERS, and enum values.
    /// </summary>
    private static readonly string[] FrozenContract =
    [
        "enum mmca.fitness.ProductReply.Availability.AVAILABILITY_IN_STOCK = 1",
        "enum mmca.fitness.ProductReply.Availability.AVAILABILITY_UNSPECIFIED = 0",
        "message mmca.fitness.GetProductRequest.culture = 2 : optional string",
        "message mmca.fitness.GetProductRequest.product_id = 1 : int32",
        "message mmca.fitness.ProductReply.Dimensions.height = 2 : double",
        "message mmca.fitness.ProductReply.Dimensions.width = 1 : double",
        "message mmca.fitness.ProductReply.availability = 4 : Availability",
        "message mmca.fitness.ProductReply.name = 2 : string",
        "message mmca.fitness.ProductReply.product_id = 1 : int32",
        "message mmca.fitness.ProductReply.tags = 3 : repeated string",
        "service mmca.fitness.CatalogService.GetProduct(GetProductRequest) returns (ProductReply)",
        "service mmca.fitness.CatalogService.StreamProducts(GetProductRequest) returns (stream ProductReply)",
    ];

    [Fact]
    public void Rule_PassesWhenTheProtoMatchesItsFrozenSnapshot()
    {
        var act = () => ArchitectureRules.ProtoContractsMatchFrozenList(PinnedProto, FrozenContract, SolutionFileName);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rule_FlagsARenumberedField()
    {
        var act = () => ArchitectureRules.ProtoContractsMatchFrozenList(DriftedProto, FrozenContract, SolutionFileName);

        var message = act.Should().Throw<Exception>().Which.Message;
        message.Should().Contain(
            "message mmca.fitness.GetProductRequest.product_id = 7 : int32",
            "the renumbered field is present but not frozen");
        message.Should().Contain(
            "message mmca.fitness.GetProductRequest.product_id = 1 : int32",
            "and the frozen number is now missing, so both sides of the change are reported");
    }

    [Fact]
    public void Rule_FlagsAnAddedRpc()
    {
        var act = () => ArchitectureRules.ProtoContractsMatchFrozenList(DriftedProto, FrozenContract, SolutionFileName);

        act.Should().Throw<Exception>().Which.Message.Should().Contain(
            "service mmca.fitness.CatalogService.DeleteProduct(GetProductRequest) returns (ProductReply)",
            "a new rpc is a contract addition peers have not been told about");
    }

    [Fact]
    public void Rule_ReportsAMissingProtoFileExplicitly()
    {
        var act = () => ArchitectureRules.ProtoContractsMatchFrozenList(
            [$"{TestDataDirectory}/does-not-exist.proto"],
            FrozenContract,
            SolutionFileName);

        act.Should().Throw<Exception>().Which.Message.Should().Contain(
            "<missing proto file> does-not-exist.proto",
            "a path typo must fail loudly rather than pin an empty contract");
    }

    [Fact]
    public void BuildProtoContract_IgnoresSyntaxImportAndOptionLines()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot(SolutionFileName);
        var contract = ArchitectureRules.BuildProtoContract([Path.Combine(repoRoot, PinnedProto[0])]);

        contract.Should().Equal(FrozenContract);
        contract.Should().NotContain(line => line.Contains("csharp_namespace", StringComparison.Ordinal));
        contract.Should().NotContain(line => line.Contains("timestamp.proto", StringComparison.Ordinal));
    }
}
