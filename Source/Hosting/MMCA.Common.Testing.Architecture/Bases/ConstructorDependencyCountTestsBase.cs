namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Single-responsibility ceiling fitness function (rubric §1). Application-layer <c>*Service</c> classes
/// coordinate a cohesive use-case facade; a ballooning constructor-dependency list is the canonical SRP
/// smell. Authored once here and re-run as a thin subclass in each repo: the subclass supplies its
/// <see cref="Map"/> (whose module Application assemblies are scanned) and its accepted
/// <see cref="MaxConstructorDependencies"/> high-water mark. The build fails if any Application service
/// constructor exceeds that ceiling, turning a previously-implicit judgement call into an enforced
/// invariant so the next service cannot silently grow past it without a deliberate decision (raise the
/// ceiling consciously if it must). Repos without business modules (MMCA.Common itself) have nothing to
/// scan and do not subclass this.
/// </summary>
public abstract class ConstructorDependencyCountTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The accepted constructor-dependency high-water mark for the repo's Application services (e.g. its
    /// <c>AuthenticationService</c> facade). Anything above it fails the build.
    /// </summary>
    protected abstract int MaxConstructorDependencies { get; }

    [Fact]
    public void ApplicationServices_DoNotExceedConstructorDependencyCeiling()
    {
        var services = Map.ModuleApplication()
            .SelectMany(static a => a.GetTypes())
            .Where(static t => t is { IsClass: true, IsAbstract: false }
                && t.Name.EndsWith("Service", StringComparison.Ordinal))
            .ToList();

        services.Should().NotBeEmpty(
            "the guard must scan at least one Application service (otherwise it passes vacuously)");

        var offenders = services
            .Select(static t => new
            {
                Type = t,
                MaxParameters = t.GetConstructors()
                    .Select(static c => c.GetParameters().Length)
                    .DefaultIfEmpty(0)
                    .Max(),
            })
            .Where(x => x.MaxParameters > MaxConstructorDependencies)
            .Select(static x => $"{x.Type.FullName} ({x.MaxParameters} ctor dependencies)")
            .ToList();

        offenders.Should().BeEmpty(
            $"Application service constructors must stay within {MaxConstructorDependencies} dependencies "
            + "(the repo's accepted high-water mark); a larger list signals a class doing too much — "
            + "split it or extract a cohesive collaborator. Offenders: "
            + string.Join(", ", offenders));
    }
}
