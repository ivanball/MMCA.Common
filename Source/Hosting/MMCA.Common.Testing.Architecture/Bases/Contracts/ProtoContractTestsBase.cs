namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Frozen wire-contract guard for a repo's gRPC <c>.proto</c> files: the synchronous counterpart to
/// <see cref="IntegrationEventContractTestsBase"/>. The subclass names its solution file, its proto
/// files, and the committed snapshot; this base rebuilds the live contract and reports the diff.
/// </summary>
/// <remarks>
/// Consumer-facing, like <see cref="NamingConventionTestsBase"/>: MMCA.Common ships no <c>.proto</c>
/// of its own (it supplies the gRPC plumbing, not the contracts), so the framework does NOT subclass
/// this. A repo with a <c>*.Contracts</c> project does, listing every proto that project compiles.
/// <para>
/// To produce or refresh <see cref="FrozenProtoContracts"/>, print
/// <c>ArchitectureRules.BuildProtoContract(...)</c> for the same files and paste the result: the
/// snapshot is meant to be regenerated deliberately, as part of the commit that changes the contract,
/// never edited to make a red test go green.
/// </para>
/// </remarks>
public abstract class ProtoContractTestsBase
{
    /// <summary>The solution file marking the repo root, e.g. <c>MMCA.Store.slnx</c>.</summary>
    protected abstract string SolutionFileName { get; }

    /// <summary>The <c>.proto</c> files to pin, as repo-root-relative paths.</summary>
    protected abstract IReadOnlyList<string> ProtoFiles { get; }

    /// <summary>
    /// The committed snapshot: one line per service rpc, message field and enum value.
    /// </summary>
    protected abstract IReadOnlyList<string> FrozenProtoContracts { get; }

    [Fact]
    public void ProtoContracts_ShouldMatch_TheFrozenSnapshot() =>
        ArchitectureRules.ProtoContractsMatchFrozenList(ProtoFiles, FrozenProtoContracts, SolutionFileName);
}
