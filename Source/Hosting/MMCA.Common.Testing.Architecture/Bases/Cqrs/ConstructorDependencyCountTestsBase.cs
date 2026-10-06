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
/// lowers it the moment remediation makes a lower number true. Both directions are enforced: the
/// <c>*_DoNotExceedConstructorDependencyCeiling</c> facts fail above a ceiling, and the
/// <c>*_ConstructorDependencyCeilingIsTight</c> facts fail when the widest class of a population sits
/// below its ceiling, naming the number to lower it to.
/// </para>
/// <para>
/// The three populations and the constructors measured on each type are virtual extension points
/// (<see cref="ScannedServices"/>, <see cref="ScannedControllers"/>, <see cref="ScannedHandlers"/>,
/// <see cref="MeasuredConstructors"/>). Their defaults are the module populations described above and
/// the public constructors, so a consumer that overrides nothing scans exactly what it always did. A
/// repo without business modules (MMCA.Common itself) overrides them to scan its framework assemblies.
/// </para>
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

    /// <summary>
    /// Gets the classes the Application-service ceiling measures. Defaults to every concrete class whose
    /// name ends in <c>Service</c> in the map's module Application assemblies.
    /// </summary>
    protected virtual IEnumerable<Type> ScannedServices =>
        Map.ModuleApplication()
            .SelectMany(static a => a.GetTypes())
            .Where(static t => t is { IsClass: true, IsAbstract: false }
                && t.Name.EndsWith("Service", StringComparison.Ordinal));

    /// <summary>
    /// Gets the classes the controller ceiling measures. Defaults to every concrete, non-nested
    /// <c>ControllerBase</c>-derived class in the map's API assemblies.
    /// </summary>
    protected virtual IEnumerable<Type> ScannedControllers => Scan(Map.Api(), IsController);

    /// <summary>
    /// Gets the classes the handler ceiling measures. Defaults to every concrete, non-nested
    /// <c>ICommandHandler&lt;,&gt;</c> / <c>IQueryHandler&lt;,&gt;</c> implementation in the map's module
    /// Application assemblies.
    /// </summary>
    protected virtual IEnumerable<Type> ScannedHandlers => Scan(Map.ModuleApplication(), IsCommandOrQueryHandler);

    /// <summary>
    /// The constructors whose parameter count is measured for a scanned type; the widest one is the
    /// type's dependency count. Defaults to the public instance constructors.
    /// </summary>
    /// <param name="type">A type from one of the scanned populations.</param>
    /// <returns>The constructors to measure.</returns>
    protected virtual IEnumerable<ConstructorInfo> MeasuredConstructors(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetConstructors();
    }

    [Fact]
    public void ApplicationServices_DoNotExceedConstructorDependencyCeiling()
    {
        var services = ScannedServices.ToList();

        services.Should().NotBeEmpty(
            "the guard must scan at least one Application service (otherwise it passes vacuously)");

        var offenders = Offenders(services, MaxConstructorDependencies);

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
        var controllers = ScannedControllers.ToList();

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
        var handlers = ScannedHandlers.ToList();

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
    /// The ratchet's other half: the Application-service ceiling must equal the widest scanned
    /// service. A ceiling left above the observed maximum is a budget, not a ratchet, because the next
    /// class can grow into the slack without anything failing; the moment remediation makes a lower
    /// number true, this fact fails and names the number to lower it to.
    /// </summary>
    [Fact]
    public void ApplicationServices_ConstructorDependencyCeilingIsTight() =>
        AssertCeilingIsTight(
            "Application services",
            nameof(MaxConstructorDependencies),
            MaxConstructorDependencies,
            ScannedServices);

    /// <summary>
    /// The controller ceiling must equal the widest scanned controller, so a ceiling cannot be left
    /// loose after a controller is slimmed down (see
    /// <see cref="ApplicationServices_ConstructorDependencyCeilingIsTight"/>).
    /// </summary>
    [Fact]
    public void Controllers_ConstructorDependencyCeilingIsTight() =>
        AssertCeilingIsTight(
            "API controllers",
            nameof(MaxControllerConstructorDependencies),
            MaxControllerConstructorDependencies,
            ScannedControllers);

    /// <summary>
    /// The handler ceiling must equal the widest scanned command or query handler, so a ceiling cannot
    /// be left loose after a handler is slimmed down (see
    /// <see cref="ApplicationServices_ConstructorDependencyCeilingIsTight"/>).
    /// </summary>
    [Fact]
    public void Handlers_ConstructorDependencyCeilingIsTight() =>
        AssertCeilingIsTight(
            "CQRS handlers",
            nameof(MaxHandlerConstructorDependencies),
            MaxHandlerConstructorDependencies,
            ScannedHandlers);

    /// <summary>
    /// Fails when the widest measured class of a population sits below the population's declared
    /// ceiling. The message names the population, the ceiling property, its value, the observed
    /// maximum and the classes at that maximum, so the fix (lower the ceiling to the observed number)
    /// is in the failure itself.
    /// </summary>
    private void AssertCeilingIsTight(string population, string ceilingName, int ceiling, IEnumerable<Type> types)
    {
        var measured = types
            .Select(t => (Type: t, Count: WidestConstructor(t)))
            .ToList();

        measured.Should().NotBeEmpty(
            $"the {population} tightness check must measure at least one class (otherwise it passes vacuously)");

        var observedMax = measured.Max(static x => x.Count);
        var widest = measured
            .Where(x => x.Count == observedMax)
            .Select(static x => x.Type.FullName)
            .Order(StringComparer.Ordinal);

        observedMax.Should().BeGreaterThanOrEqualTo(
            ceiling,
            $"the {population} ceiling is a ratchet, not a budget: {ceilingName} is {ceiling} but the widest "
            + $"{population} constructor takes {observedMax} ({string.Join(", ", widest)}). Lower "
            + $"{ceilingName} from {ceiling} to {observedMax} so the next class cannot grow into the slack");
    }

    /// <summary>The parameter count of the widest measured constructor of a type (0 when none).</summary>
    private int WidestConstructor(Type type) =>
        MeasuredConstructors(type)
            .Select(static c => c.GetParameters().Length)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// The concrete, non-nested, non-compiler-generated classes of the given assemblies that match the
    /// population predicate.
    /// </summary>
    private static List<Type> Scan(IEnumerable<Assembly> assemblies, Func<Type, bool> isInPopulation) =>
        [.. assemblies
            .SelectMany(static a => a.ConcreteClasses)
            .Where(static t => !t.IsNested && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Where(isInPopulation)];

    /// <summary>Formats the types whose widest measured constructor exceeds the ceiling.</summary>
    private List<string> Offenders(IEnumerable<Type> types, int ceiling) =>
        [.. types
            .Select(t => new
            {
                Type = t,
                MaxParameters = WidestConstructor(t),
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
