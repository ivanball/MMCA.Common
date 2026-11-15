namespace MMCA.Common.Architecture.Tests.Governance.TestGaps;

/// <summary>
/// A throwaway repository tree under the system temp folder for the source-scanning governance
/// rules, which read files rather than IL. Each instance owns a unique folder and deletes it on
/// dispose, so tests running in parallel never share a tree.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    public TempRepo() =>
        Root = Directory.CreateTempSubdirectory("mmca-testgaps-").FullName;

    /// <summary>The absolute path of the tree's root.</summary>
    public string Root { get; }

    /// <summary>Writes a file at a repo-relative path (forward slashes), creating its folders.</summary>
    public TempRepo Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A scanner still holding a handle on a slow file system must not fail the test that
            // already passed; the OS temp cleanup reclaims the folder.
        }
    }
}
