namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Folder-width fitness function (rubric §5): module by project, feature by folder, use case by
    /// leaf. A folder that accumulates dozens of direct code files has stopped naming a feature and
    /// started naming a technical bucket, which is exactly the horizontal layout vertical slicing
    /// exists to avoid. This rule walks the repository's <c>Source/</c> and <c>Tests/</c> trees from
    /// the filesystem (not IL, because the defect is a layout one) and fails the build for every
    /// directory holding more than <paramref name="maxDirectFiles"/> direct code files.
    /// <para>
    /// <b>What counts.</b> Files directly in the directory only, never its subdirectories. A
    /// <c>.razor</c> file counts once; its co-located code-behind <c>X.razor.cs</c> counts with it
    /// rather than separately, so a component is one unit however many partial files back it. Every
    /// other <c>.cs</c> file counts one. Resource files (<c>.resx</c>) never count, since a page and
    /// its localization satellites are a single authoring unit. Generated files
    /// (<c>*.g.cs</c>, <c>*.generated.cs</c>, <c>*.Designer.cs</c>) never count, because nobody
    /// chose to put them there.
    /// </para>
    /// <para>
    /// <b>What is skipped.</b> Any directory with a path segment named <c>bin</c>, <c>obj</c>,
    /// <c>Migrations</c>, <c>Platforms</c>, <c>Resources</c>, <c>node_modules</c>, <c>wwwroot</c> or
    /// <c>.git</c>: build output and tool-owned or platform-owned trees whose shape is not the
    /// author's decision. On top of that, <paramref name="exemptFolderSuffixes"/> lets a repo carry
    /// its own documented exemptions, matched against the directory's repo-relative path written with
    /// forward slashes (for example <c>Testing.Architecture/Bases</c>), so an exemption can name a
    /// specific folder rather than every folder that happens to share a leaf name.
    /// </para>
    /// </summary>
    /// <param name="repoRoot">The repository root, typically from <see cref="ArchitectureMapBase.FindRepoRoot(string)"/>.</param>
    /// <param name="maxDirectFiles">The highest number of direct code files a folder may hold.</param>
    /// <param name="exemptFolderSuffixes">Repo-relative folder path suffixes (forward slashes) exempt from the rule.</param>
    public static void FoldersStayNarrow(string repoRoot, int maxDirectFiles, IReadOnlyCollection<string> exemptFolderSuffixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDirectFiles, 1);
        ArgumentNullException.ThrowIfNull(exemptFolderSuffixes);

        var offenders = new List<string>();

        foreach (var treeName in new[] { "Source", "Tests" })
        {
            var tree = Path.Combine(repoRoot, treeName);
            if (!Directory.Exists(tree))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(tree, "*", SearchOption.AllDirectories).Prepend(tree))
            {
                var relative = RepoRelativeFolder(repoRoot, directory);
                if (IsSkippedFolder(relative) || IsExemptFolder(relative, exemptFolderSuffixes))
                {
                    continue;
                }

                var count = DirectCodeUnitCount(directory);
                if (count > maxDirectFiles)
                {
                    offenders.Add($"  - {relative}: {count} direct code files (max {maxDirectFiles})");
                }
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(offenders,
            $"a folder holds at most {maxDirectFiles} direct code files (feature-by-folder layout, rubric §5); "
                + "split it by feature or aggregate");
    }

    /// <summary>The directory's path relative to the repo root, written with forward slashes.</summary>
    private static string RepoRelativeFolder(string repoRoot, string directory) =>
        Path.GetRelativePath(repoRoot, directory).Replace('\\', '/');

    /// <summary>True for build output and tool-owned or platform-owned trees, whose shape nobody chose.</summary>
    private static bool IsSkippedFolder(string relativeFolder) =>
        relativeFolder
            .Split('/')
            .Any(static segment =>
                segment is "bin" or "obj" or "Migrations" or "Platforms" or "Resources" or "node_modules" or "wwwroot" or ".git");

    /// <summary>True when the folder matches one of the repo's documented exemptions.</summary>
    private static bool IsExemptFolder(string relativeFolder, IReadOnlyCollection<string> exemptFolderSuffixes) =>
        exemptFolderSuffixes.Any(suffix => relativeFolder.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>The number of direct code units in a directory, with each razor component counted once.</summary>
    private static int DirectCodeUnitCount(string directory)
    {
        var files = Directory.GetFiles(directory);

        var razorFileNames = files
            .Select(Path.GetFileName)
            .Where(static name => name is not null && name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return files.Count(file => IsCodeUnit(Path.GetFileName(file), razorFileNames));
    }

    /// <summary>
    /// True when a file name is a code unit in its own right: any <c>.razor</c> file, or a
    /// non-generated <c>.cs</c> file that is not the code-behind of a <c>.razor</c> file beside it.
    /// </summary>
    private static bool IsCodeUnit(string fileName, HashSet<string?> razorFileNames)
    {
        if (fileName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsGeneratedFile(fileName))
        {
            return false;
        }

        // X.razor.cs is part of the X.razor unit whenever that component sits beside it.
        return !(fileName.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase)
            && razorFileNames.Contains(fileName[..^".cs".Length]));
    }

    /// <summary>True for a generated file, which its author never placed in the folder by hand.</summary>
    private static bool IsGeneratedFile(string fileName) =>
        fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
}
