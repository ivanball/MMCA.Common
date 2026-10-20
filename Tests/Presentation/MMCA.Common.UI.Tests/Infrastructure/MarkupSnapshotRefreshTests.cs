using System.Runtime.CompilerServices;
using AwesomeAssertions;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// L194: a run with <c>UPDATE_SNAPSHOTS=1</c> rewrites the baseline but never counts as a match,
/// matching the OpenAPI baseline gate's refresh path, so a CI job or a shell that leaves the variable
/// set cannot turn every markup regression green while silently rewriting the baselines.
/// </summary>
[Collection(SnapshotRefreshCollection.Name)]
public sealed class MarkupSnapshotRefreshTests
{
    private const string Variable = "UPDATE_SNAPSHOTS";

    [Fact]
    public void Refresh_WritesTheBaseline_AndReportsANonMatch()
    {
        // Match resolves Snapshots/ next to its caller, which is this file; a unique name keeps the
        // probe baseline apart from the committed ones and the finally removes it.
        var name = $"RefreshProbe-{Guid.NewGuid():N}";
        var snapshots = Path.Combine(Path.GetDirectoryName(ThisFile())!, "Snapshots");
        var baseline = Path.Combine(snapshots, name + ".html");
        var createdFolder = !Directory.Exists(snapshots);
        Directory.CreateDirectory(snapshots);
        File.WriteAllText(baseline, "<p>old</p>");

        var previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, "1");
        try
        {
            var refreshed = MarkupSnapshot.Match("<p>new</p>", name);

            refreshed.IsMatch.Should().BeFalse("a regenerating run must never count as a pass");
            refreshed.Message.Should().Contain("refreshed").And.Contain("review and commit");
            File.ReadAllText(baseline).Should().Be("<p>new</p>", "the refresh still writes the new baseline");

            Environment.SetEnvironmentVariable(Variable, previous);
            MarkupSnapshot.Match("<p>new</p>", name).IsMatch.Should().BeTrue(
                "the next run without the variable compares against the refreshed baseline");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
            File.Delete(baseline);
            if (createdFolder)
            {
                Directory.Delete(snapshots);
            }
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}

/// <summary>
/// Serializes the test that sets <c>UPDATE_SNAPSHOTS</c>: the variable is process-wide, so a snapshot
/// test running beside it would refresh its own baseline instead of comparing against it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SnapshotRefreshCollection
{
    public const string Name = "Snapshot-refresh tests";
}
