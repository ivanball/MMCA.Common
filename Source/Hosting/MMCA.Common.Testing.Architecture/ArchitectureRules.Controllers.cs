namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>Module controllers must not reach into Infrastructure — they go through Application handlers.</summary>
    public static void ControllersDoNotDependOnInfrastructure(IArchitectureMap map)
    {
        foreach (var apiRef in map.Layers.Where(l => l.Layer == Layer.Api && l.Module.Length > 0))
        {
            var forbidden = map.RootNamespace(apiRef.Module, Layer.Infrastructure);
            var result = Types.InAssembly(apiRef.Assembly)
                .That().HaveNameEndingWith("Controller")
                .ShouldNot().HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{apiRef.RootNamespace}: controllers must not depend on Infrastructure — use Application handlers");
        }
    }

    /// <summary>Module controllers must not reach EF Core directly — data access belongs in Infrastructure.</summary>
    public static void ControllersDoNotDependOnEntityFrameworkCore(IArchitectureMap map)
    {
        foreach (var apiRef in map.Layers.Where(l => l.Layer == Layer.Api && l.Module.Length > 0))
        {
            var result = Types.InAssembly(apiRef.Assembly)
                .That().HaveNameEndingWith("Controller")
                .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore")
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{apiRef.RootNamespace}: controllers must not depend on EF Core — data access belongs in Infrastructure");
        }
    }

    /// <summary>Controllers are sealed (across framework + module API assemblies).</summary>
    public static void ControllersAreSealed(IArchitectureMap map)
    {
        var offenders = map.Api()
            .SelectMany(a => a.ConcreteClasses())
            .Where(IsController)
            .Where(t => !t.IsSealed)
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "controllers must be sealed — they are leaf presentation types, not inheritance roots");
    }

    /// <summary>
    /// Module controllers inherit the framework <c>ApiControllerBase</c> (RFC 9457 failure mapping).
    /// <paramref name="exemptFullNames"/> lists controllers that legitimately bypass it (e.g. a payment
    /// webhook endpoint that owns its own response semantics).
    /// </summary>
    public static void ControllersInheritApiControllerBase(IArchitectureMap map, IEnumerable<string>? exemptFullNames = null)
    {
        var exempt = exemptFullNames is null ? [] : exemptFullNames.ToHashSet(StringComparer.Ordinal);
        var offenders = map.Layers
            .Where(l => l.Layer == Layer.Api && l.Module.Length > 0)
            .SelectMany(l => l.Assembly.ConcreteClasses())
            .Where(IsController)
            .Where(t => !exempt.Contains(t.FullName ?? t.Name))
            .Where(t => !t.HasBaseTypeStartingWith("MMCA.Common.API.Controllers.ApiControllerBase")
                && !t.HasBaseTypeStartingWith("MMCA.Common.API.Controllers.EntityControllerBase"))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "controllers must inherit ApiControllerBase/EntityControllerBase (consistent Result → HTTP mapping), not raw ControllerBase");
    }

    private static bool IsController(Type type) =>
        type.SimpleName().EndsWith("Controller", StringComparison.Ordinal)
        || type.HasBaseTypeStartingWith("Microsoft.AspNetCore.Mvc.ControllerBase")
        || type.HasBaseTypeStartingWith("Microsoft.AspNetCore.Mvc.Controller");
}
