namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Cross-module project-reference fitness function: every <c>ProjectReference</c> from one module's
    /// project to ANOTHER module's project must appear in an explicit allowlist, and every allowlist
    /// entry must still exist. The module-isolation rules read IL, so a coupling that leaves no reference
    /// in the compiled assembly (a <see langword="const"/> inlined at its use site, a
    /// <c>global using</c> type alias) passes them while the project graph still couples the two
    /// modules at build time. This rule reads the <c>.csproj</c> files instead, so the cross-module
    /// graph a module extraction (ADR-007/008) has to honor is a declared, reviewed fact rather than
    /// whatever the project files happen to say.
    /// <para>
    /// <b>What is scanned.</b> Every <c>.csproj</c> under <paramref name="modulesFolder"/> (outside
    /// <c>bin</c> and <c>obj</c>), each named <c>{RepoToken}.{Module}.{Layer}</c>. An edge is written
    /// <c>"{Module}.{Layer} -&gt; {Module}.{Layer}"</c>, for example
    /// <c>"Conference.Application -&gt; Engagement.Shared"</c>. References within one module, and
    /// references to projects outside the modules folder (hosts, <c>*.Contracts</c>, the framework), are
    /// not cross-module module edges and are ignored.
    /// </para>
    /// <para>
    /// <b>Why it cannot pass vacuously.</b> The modules found on disk must equal
    /// <paramref name="moduleNames"/> (the architecture map's declared set), and that set must not be
    /// empty, so a wrong repo root, a moved modules folder or a renamed project fails instead of
    /// scanning nothing and reporting green.
    /// </para>
    /// </summary>
    /// <param name="repoRoot">The repository root, typically from <see cref="ArchitectureMapBase.FindRepoRoot(string)"/>.</param>
    /// <param name="modulesFolder">The repo-relative folder holding the module projects, for example <c>Source/Modules</c>.</param>
    /// <param name="repoToken">The repo's project-name prefix, for example <c>MMCA.ADC</c>.</param>
    /// <param name="moduleNames">The module names the repo's architecture map declares.</param>
    /// <param name="allowedReferences">The permitted cross-module edges, each written <c>"{Module}.{Layer} -&gt; {Module}.{Layer}"</c>.</param>
    public static void CrossModuleReferencesMatchAllowlist(
        string repoRoot,
        string modulesFolder,
        string repoToken,
        IReadOnlyCollection<string> moduleNames,
        IReadOnlyCollection<string> allowedReferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoToken);
        ArgumentNullException.ThrowIfNull(moduleNames);
        ArgumentNullException.ThrowIfNull(allowedReferences);

        const string reason =
            "every cross-module ProjectReference must be declared in the allowlist, and every allowlist entry must "
            + "still exist (entries are written \"{Module}.{Layer} -> {Module}.{Layer}\")";

        var violations = new List<string>();
        var modulesRoot = Path.Combine(repoRoot, modulesFolder);

        if (moduleNames.Count == 0)
        {
            violations.Add("  - the architecture map declares no modules, so the rule would check nothing");
        }

        if (!Directory.Exists(modulesRoot))
        {
            violations.Add($"  - modules folder not found: {modulesFolder}");
            ArchitectureAssert.NoViolations(violations, reason);
            return;
        }

        var projects = DiscoverModuleProjects(repoRoot, modulesRoot, repoToken, violations);

        CompareDiscoveredModules(projects.Values.Select(static p => p.Module), moduleNames, violations);

        var observed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects.Values)
        {
            foreach (var targetName in ProjectReferenceNames(project.FullPath))
            {
                if (projects.TryGetValue(targetName, out var target)
                    && !string.Equals(target.Module, project.Module, StringComparison.Ordinal))
                {
                    observed.TryAdd($"{project.Module}.{project.Layer} -> {target.Module}.{target.Layer}", project.RelativePath);
                }
            }
        }

        var allowed = allowedReferences.ToHashSet(StringComparer.Ordinal);
        violations.AddRange(observed
            .Where(edge => !allowed.Contains(edge.Key))
            .Select(edge => $"  - undeclared cross-module reference: {edge.Key} (in {edge.Value})"));
        violations.AddRange(allowed
            .Where(entry => !observed.ContainsKey(entry))
            .Order(StringComparer.Ordinal)
            .Select(entry => $"  - allowlist entry matches no ProjectReference (stale or misspelled): {entry}"));

        ArchitectureAssert.NoViolations(violations, reason);
    }

    /// <summary>
    /// The module projects under <paramref name="modulesRoot"/>, keyed by project name. A project that does
    /// not follow <c>{RepoToken}.{Module}.{Layer}</c> is reported rather than skipped, because a project
    /// the rule cannot place is a project whose references it cannot check.
    /// </summary>
    private static Dictionary<string, ModuleProject> DiscoverModuleProjects(
        string repoRoot, string modulesRoot, string repoToken, List<string> violations)
    {
        var prefix = repoToken + ".";
        var projects = new Dictionary<string, ModuleProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(modulesRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            if (relativePath.Split('/').Any(static segment => segment is "bin" or "obj"))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var rest = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : string.Empty;
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == rest.Length - 1)
            {
                violations.Add($"  - module project not named {repoToken}.{{Module}}.{{Layer}}: {relativePath}");
                continue;
            }

            projects[name] = new ModuleProject(rest[..dot], rest[(dot + 1)..], path, relativePath);
        }

        return projects;
    }

    /// <summary>Reports any module present on disk but not declared by the map, and the reverse.</summary>
    private static void CompareDiscoveredModules(
        IEnumerable<string> discoveredModules, IReadOnlyCollection<string> moduleNames, List<string> violations)
    {
        var onDisk = discoveredModules.ToHashSet(StringComparer.Ordinal);
        var declared = moduleNames.ToHashSet(StringComparer.Ordinal);

        violations.AddRange(onDisk.Except(declared).Order(StringComparer.Ordinal)
            .Select(module => $"  - module found on disk but not declared by the architecture map: {module}"));
        violations.AddRange(declared.Except(onDisk).Order(StringComparer.Ordinal)
            .Select(module => $"  - module declared by the architecture map but no project found on disk: {module}"));
    }

    /// <summary>The project names (file name without extension) of every <c>ProjectReference</c> in a project file.</summary>
    private static IEnumerable<string> ProjectReferenceNames(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants()
            .Where(static element => element.Name.LocalName == "ProjectReference")
            .Select(static element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(static include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

    /// <summary>One module project: its module, its layer segment and where it lives.</summary>
    private sealed record ModuleProject(string Module, string Layer, string FullPath, string RelativePath);
}
