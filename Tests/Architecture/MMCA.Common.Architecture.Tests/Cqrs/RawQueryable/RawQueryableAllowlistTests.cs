using MMCA.Common.Architecture.Tests.Governance.TestGaps;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Cqrs.RawQueryable;

/// <summary>
/// Self-test for the <see cref="RawQueryableConventionTestsBase"/> allowlist. Each fact points the
/// scan at a throwaway source tree and pins: an allowlisted file that still uses a raw queryable
/// surface is exempt, an unlisted one is flagged, and an entry that names no scanned file, or a file
/// with no raw queryable use left, is reported as stale. The probe subclass is private so xUnit does
/// not collect its inherited fact as a test of its own.
/// </summary>
public sealed class RawQueryableAllowlistTests
{
    private const string RawUse = "var rows = repository.TableNoTracking.Where(x => x.Id == id);";
    private const string CleanUse = "var rows = await repository.GetAllAsync(cancellationToken);";

    [Fact]
    public void AllowlistedFileWithRawUse_IsExempt()
    {
        using var repo = new TempRepo()
            .Write("App/AllowedHandler.cs", RawUse)
            .Write("App/CleanHandler.cs", CleanUse);

        var act = new ScanProbe(repo.Root, ["AllowedHandler.cs"]).ApplicationLayer_DoesNotUseRawQueryableSurfaces;

        act.Should().NotThrow("the allowlisted file still needs its exemption and no other file uses a raw surface");
    }

    [Fact]
    public void UnlistedFileWithRawUse_IsFlagged()
    {
        using var repo = new TempRepo().Write("App/NewHandler.cs", RawUse);

        var act = new ScanProbe(repo.Root, []).ApplicationLayer_DoesNotUseRawQueryableSurfaces;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("NewHandler.cs:1", "a raw queryable use outside the allowlist is the violation the rule exists for");
    }

    [Fact]
    public void AllowlistEntryNamingNoFile_IsFlaggedAsStale()
    {
        using var repo = new TempRepo().Write("App/CleanHandler.cs", CleanUse);

        var act = new ScanProbe(repo.Root, ["GhostHandler.cs"]).ApplicationLayer_DoesNotUseRawQueryableSurfaces;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                "GhostHandler.cs is in AllowedFiles but no scanned file of that name uses a raw IQueryable surface",
                "an entry for a file that no longer exists is stale and must be removed");
    }

    [Fact]
    public void AllowlistEntryForAFileWithNoRawUse_IsFlaggedAsStale()
    {
        using var repo = new TempRepo().Write("App/CleanHandler.cs", CleanUse);

        var act = new ScanProbe(repo.Root, ["CleanHandler.cs"]).ApplicationLayer_DoesNotUseRawQueryableSurfaces;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                "CleanHandler.cs is in AllowedFiles but no scanned file of that name uses a raw IQueryable surface",
                "an entry for a file that has been cleaned would let a raw use come back unnoticed");
    }

    /// <summary>Scans one temp folder with a given allowlist.</summary>
    private sealed class ScanProbe(string root, IReadOnlyList<string> allowedFiles) : RawQueryableConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

        protected override IReadOnlyList<string> AllowedFiles => allowedFiles;

        protected override IEnumerable<string> ApplicationSourceDirectories()
        {
            yield return root;
        }
    }
}
