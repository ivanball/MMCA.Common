using System.Runtime.CompilerServices;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Single-responsibility ceiling fitness function (rubric §1), applied to the three injection points a
/// module actually composes: Application-layer <c>*Service</c> facades, API controllers, and CQRS
/// command/query handlers. Each coordinates one cohesive unit of work, so a ballooning
/// constructor-dependency list is the canonical SRP smell wherever it appears. Authored once here and
/// re-run as a thin subclass in each repo: the subclass supplies its <see cref="Map"/> (whose module
/// Application assemblies and API assemblies are scanned) and one accepted high-water mark per
/// population.
/// <para>
/// Each ceiling is a RATCHET, not a budget: a consumer sets it at its current high-water mark so the
/// next class cannot silently grow past it, raises it only as a recorded deliberate decision, and
/// lowers it the moment remediation makes a lower number true.
/// </para>
/// Repos without business modules (MMCA.Common itself) have nothing to scan and do not subclass this.
/// </summary>
public abstract class ConstructorDependencyCountTestsBase
{
    // Matched by full name so the rule library keeps zero compile dependencies on ASP.NET Core or on
    // MMCA.Common.Application (see the csproj comment): the assemblies come from the consumer's map.
    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string CommandHandlerInterfacePrefix = "MMCA.Common.Application.UseCases.Contracts.ICommandHandler";
    private const string QueryHandlerInterfacePrefix = "MMCA.Common.Application.UseCases.Contracts.IQueryHandler";

    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The accepted constructor-dependency high-water mark for the repo's Application services (e.g. its
    /// <c>AuthenticationService</c> facade). Anything above it fails the build.
    /// </summary>
    protected abstract int MaxConstructorDependencies { get; }

    /// <summary>
    /// The accepted constructor-dependency high-water mark for the repo's API controllers (every concrete
    /// class deriving from <c>ControllerBase</c> in the map's API assemblies). Anything above it fails the
    /// build. Abstract on purpose: a ceiling nobody chose is not a ceiling.
    /// </summary>
    protected abstract int MaxControllerConstructorDependencies { get; }

    /// <summary>
    /// The accepted constructor-dependency high-water mark for the repo's CQRS handlers (every concrete
    /// <c>ICommandHandler&lt;,&gt;</c> / <c>IQueryHandler&lt;,&gt;</c> implementation in the map's module
    /// Application assemblies). Anything above it fails the build. Abstract on purpose: a ceiling nobody
    /// chose is not a ceiling.
    /// </summary>
    protected abstract int MaxHandlerConstructorDependencies { get; }

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

    /// <summary>
    /// Every concrete <c>ControllerBase</c>-derived class in the map's API assemblies (framework plus
    /// module) stays within <see cref="MaxControllerConstructorDependencies"/>. A controller is a thin
    /// translation layer over Application handlers, so a long injection list means it is serving more
    /// than one resource.
    /// </summary>
    [Fact]
    public void Controllers_DoNotExceedConstructorDependencyCeiling()
    {
        var controllers = Scan(Map.Api(), IsController);

        controllers.Should().NotBeEmpty(
            "the guard must scan at least one API controller (otherwise it passes vacuously)");

        var offenders = Offenders(controllers, MaxControllerConstructorDependencies);

        offenders.Should().BeEmpty(
            "API controllers (every concrete ControllerBase-derived class in the map's API assemblies) "
            + $"must stay within {MaxControllerConstructorDependencies} constructor dependencies (the "
            + "repo's accepted high-water mark); a larger list signals a controller serving more than one "
            + "resource — split it by sub-resource or extract a cohesive collaborator. Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every concrete <c>ICommandHandler&lt;,&gt;</c> / <c>IQueryHandler&lt;,&gt;</c> implementation in
    /// the map's module Application assemblies stays within
    /// <see cref="MaxHandlerConstructorDependencies"/>. A handler serves exactly one use case, so it is
    /// the population where a long injection list is least defensible.
    /// </summary>
    [Fact]
    public void Handlers_DoNotExceedConstructorDependencyCeiling()
    {
        var handlers = Scan(Map.ModuleApplication(), IsCommandOrQueryHandler);

        handlers.Should().NotBeEmpty(
            "the guard must scan at least one command/query handler (otherwise it passes vacuously)");

        var offenders = Offenders(handlers, MaxHandlerConstructorDependencies);

        offenders.Should().BeEmpty(
            "CQRS handlers (every concrete ICommandHandler/IQueryHandler implementation in the map's "
            + $"module Application assemblies) must stay within {MaxHandlerConstructorDependencies} "
            + "constructor dependencies (the repo's accepted high-water mark); a larger list signals a "
            + "handler doing more than its one use case — split it by sub-resource or extract a cohesive "
            + "collaborator. Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The concrete, non-nested, non-compiler-generated classes of the given assemblies that match the
    /// population predicate.
    /// </summary>
    private static List<Type> Scan(IEnumerable<Assembly> assemblies, Func<Type, bool> isInPopulation) =>
        [.. assemblies
            .SelectMany(static a => a.ConcreteClasses)
            .Where(static t => !t.IsNested && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Where(isInPopulation)];

    /// <summary>Formats the types whose widest constructor exceeds the ceiling.</summary>
    private static List<string> Offenders(IEnumerable<Type> types, int ceiling) =>
        [.. types
            .Select(static t => new
            {
                Type = t,
                MaxParameters = t.GetConstructors()
                    .Select(static c => c.GetParameters().Length)
                    .DefaultIfEmpty(0)
                    .Max(),
            })
            .Where(x => x.MaxParameters > ceiling)
            .Select(static x => $"{x.Type.FullName} ({x.MaxParameters} ctor dependencies)")];

    private static bool IsController(Type type) =>
        type.HasBaseTypeStartingWith(ControllerBaseFullName);

    private static bool IsCommandOrQueryHandler(Type type) =>
        type.GetInterfaces().Any(static i => i.IsGenericType
            && i.GetGenericTypeDefinition().FullName is { } contract
            && (contract.StartsWith(CommandHandlerInterfacePrefix, StringComparison.Ordinal)
                || contract.StartsWith(QueryHandlerInterfacePrefix, StringComparison.Ordinal)));
}
