using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// State-management convention fitness function (rubric §19): Blazor Server shares one process across
/// every circuit, so user/session state must live in per-circuit scoped services — never in mutable
/// <see langword="static"/> members, which silently leak one user's state to another. Two rules hold the line.
/// (1) Reflection over the repo's <see cref="Layer.Ui"/> assemblies fails the build on any mutable
/// static field or settable static property (compiler-generated members and recorded
/// <see cref="AllowedStaticMembers"/> excepted). (2) A source scan fails the build if a production UI
/// project registers a stateful service (<c>*StateService</c>/<c>*StateContainer</c>) as a singleton —
/// the scoped-lifetime convention those services rely on. Authored once here and re-run as a thin
/// subclass in each repo; the subclass's <see cref="Map"/> must register its UI assemblies under
/// <see cref="Layer.Ui"/> (the first rule asserts this non-vacuously).
/// </summary>
public abstract partial class StateManagementConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Fully-qualified <c>Type.FullName.MemberName</c> entries deliberately exempted from the mutable
    /// static-state rule (each should carry a recorded reason in the subclass). Empty by default.
    /// </summary>
    protected virtual IReadOnlyList<string> AllowedStaticMembers => [];

    [Fact]
    public void UiAssemblies_CarryNoMutableStaticState()
    {
        var uiAssemblies = Map.OfLayer(Layer.Ui).ToArray();

        uiAssemblies.Should().NotBeEmpty(
            because: "the §19 gate reflects over the repo's UI assemblies; register them under Layer.Ui in the architecture map (a repo without UI assemblies should not subclass this base)");

        var offenders = new List<string>();
        foreach (var assembly in uiAssemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsEnum || type.IsInterface || IsCompilerGenerated(type))
                {
                    continue;
                }

                var mutableFields = type
                    .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => f is { IsInitOnly: false, IsLiteral: false } && !IsCompilerGenerated(f));

                var settableProperties = type
                    .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(static p => p.SetMethod is not null);

                offenders.AddRange(mutableFields
                    .Select(f => $"{type.FullName}.{f.Name}")
                    .Concat(settableProperties.Select(p => $"{type.FullName}.{p.Name}"))
                    .Where(member => !AllowedStaticMembers.Contains(member, StringComparer.Ordinal)));
            }
        }

        offenders.Should().BeEmpty(
            because: "UI assemblies must carry no mutable static state — in Blazor Server a static member is shared across every user's circuit, so user/session state belongs in scoped services (§19). Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void UiProjects_RegisterStatefulServicesScoped()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        var sourceDir = Path.Combine(repoRoot, "Source");

        var offenders = FindSingletonStateRegistrations(sourceDir, out var scannedUiFiles);

        scannedUiFiles.Should().BeGreaterThan(0,
            because: "the scan must see the repo's UI sources or it verifies nothing");
        offenders.Should().BeEmpty(
            because: "stateful UI services (*StateService/*StateContainer) hold per-user state and must be registered scoped, never singleton (§19). Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Scans the production UI sources under <paramref name="sourceDir"/> for a singleton registration
    /// of a stateful service. Paths are judged RELATIVE to <paramref name="sourceDir"/>, segment by
    /// segment: a file counts when one segment is a <c>*.UI</c> / <c>*.UI.*</c> project directory and no
    /// segment contains <c>Testing</c> or is <c>bin</c>/<c>obj</c>, so where the checkout lives cannot
    /// skip it. The match runs over the file text up to the end of the statement, so a registration
    /// wrapped across lines is caught.
    /// </summary>
    /// <param name="sourceDir">The repo's <c>Source</c> directory.</param>
    /// <param name="scannedUiFiles">How many UI source files were scanned.</param>
    /// <returns>One <c>File.cs:line</c> entry per offending registration.</returns>
    protected static IReadOnlyList<string> FindSingletonStateRegistrations(string sourceDir, out int scannedUiFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);

        var offenders = new List<string>();
        scannedUiFiles = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = Path.GetRelativePath(sourceDir, file)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)[..^1];
            if (!segments.Any(static s => UiProjectSegment.IsMatch(s)) ||
                segments.Any(static s => s.Contains("Testing", StringComparison.Ordinal) || s is "bin" or "obj"))
            {
                continue;
            }

            scannedUiFiles++;
            var text = File.ReadAllText(file);
            foreach (Match match in SingletonStateRegistration.Matches(text))
            {
                var line = 1 + text.AsSpan(0, match.Index).Count('\n');
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        return offenders;
    }

    [GeneratedRegex(@"^[\w.]+\.UI(\.[\w.]+)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UiProjectSegment { get; }

    // Up to the end of the statement, so the generic, the factory and the typeof forms all match and
    // a registration wrapped across lines is still one match.
    [GeneratedRegex(@"AddSingleton\b[^;]*?State(Service|Container)\b", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SingletonStateRegistration { get; }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null).Select(static t => t!);
        }
    }

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.Name.Contains('<', StringComparison.Ordinal) ||
        member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
}
