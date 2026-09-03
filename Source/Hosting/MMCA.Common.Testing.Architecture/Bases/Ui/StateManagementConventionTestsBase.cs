using System.Runtime.CompilerServices;

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
public abstract class StateManagementConventionTestsBase
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

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                !file.Contains(".UI", StringComparison.Ordinal) ||
                file.Contains("Testing", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("AddSingleton", StringComparison.Ordinal) &&
                    (lines[i].Contains("StateService", StringComparison.Ordinal) ||
                     lines[i].Contains("StateContainer", StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "stateful UI services (*StateService/*StateContainer) hold per-user state and must be registered scoped, never singleton (§19). Offenders: "
            + string.Join(", ", offenders));
    }

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
