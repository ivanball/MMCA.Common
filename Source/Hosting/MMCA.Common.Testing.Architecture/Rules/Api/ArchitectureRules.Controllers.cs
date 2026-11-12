namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string EntityControllerBaseDefinitionFullName = "MMCA.Common.API.Controllers.EntityControllerBase`3";
    private const string AllowAnonymousAttributeFullName = "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute";
    private const string OutputCacheAttributeFullName = "Microsoft.AspNetCore.OutputCaching.OutputCacheAttribute";
    private const string SupportsIfMatchAttributeFullName = "MMCA.Common.API.Concurrency.SupportsIfMatchAttribute";
    private const string ProducesResponseTypeAttributeFullName = "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute";

    /// <summary>The base read actions <c>[PublicCachedReads]</c> marks (both <c>GetAllAsync</c> overloads share a name).</summary>
    private static readonly string[] BaseReadActionNames = ["GetAllAsync", "GetAllForLookupAsync", "GetByIdAsync"];

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
            .SelectMany(a => a.ConcreteClasses)
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
            .SelectMany(l => l.Assembly.ConcreteClasses)
            .Where(IsController)
            .Where(t => !exempt.Contains(t.FullName ?? t.Name))
            .Where(t => !t.HasBaseTypeStartingWith("MMCA.Common.API.Controllers.ApiControllerBase")
                && !t.HasBaseTypeStartingWith("MMCA.Common.API.Controllers.EntityControllerBase"))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "controllers must inherit ApiControllerBase/EntityControllerBase (consistent Result → HTTP mapping), not raw ControllerBase");
    }

    /// <summary>
    /// No controller overrides a base read action of <c>EntityControllerBase</c> (both
    /// <c>GetAllAsync</c> overloads, <c>GetAllForLookupAsync</c>, <c>GetByIdAsync</c>) and puts
    /// <c>[AllowAnonymous]</c> or <c>[OutputCache]</c> on the override: <c>[PublicCachedReads]</c> on the
    /// class marks the inherited reads instead, so an override exists only for a real body.
    /// </summary>
    /// <param name="controllerTypes">The controller types to scan; only methods each type declares itself are read.</param>
    /// <remarks>
    /// Matching is by full type name (the open base <c>MMCA.Common.API.Controllers.EntityControllerBase`3</c>
    /// and the two attribute names), keeping this package free of an ASP.NET reference. An
    /// app-declared action may carry both markers: only overrides of the base reads are governed.
    /// </remarks>
    public static void ReadOverridesDoNotRedeclarePublicCaching(IEnumerable<Type> controllerTypes)
    {
        ArgumentNullException.ThrowIfNull(controllerTypes);

        var offenders = controllerTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(IsBaseReadOverride)
                .Where(m => HasAttributeWithFullName(m, AllowAnonymousAttributeFullName)
                    || HasAttributeWithFullName(m, OutputCacheAttributeFullName))
                .Select(m => $"  - {t.FullName}.{m.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(offenders,
            "a base read override must not add [AllowAnonymous] or [OutputCache]: put [PublicCachedReads(policyName)] on the controller "
            + "and delete the attribute-only override, or keep an override with a real body and drop the two markers");
    }

    /// <summary>
    /// No action carrying <c>[SupportsIfMatch]</c> declares a 412 or 428 response by hand: the attribute
    /// contributes both, typed as Problem Details, after the action's own response types.
    /// </summary>
    /// <param name="controllerTypes">The controller types to scan; only methods each type declares itself are read.</param>
    /// <remarks>
    /// Matching is by full type name: <c>MMCA.Common.API.Concurrency.SupportsIfMatchAttribute</c>, and
    /// any attribute deriving from <c>Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute</c> (the
    /// generic form included) whose <c>StatusCode</c> reads 412 or 428.
    /// </remarks>
    public static void ConditionalActionsDoNotRedeclarePreconditionResponses(IEnumerable<Type> controllerTypes)
    {
        ArgumentNullException.ThrowIfNull(controllerTypes);

        var offenders = controllerTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => HasAttributeWithFullName(m, SupportsIfMatchAttributeFullName))
                .Where(DeclaresPreconditionResponse)
                .Select(m => $"  - {t.FullName}.{m.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(offenders,
            "an action carrying [SupportsIfMatch] must not declare 412 or 428 by hand: the attribute documents both, "
            + "so delete the [ProducesResponseType] lines for those two codes");
    }

    /// <summary>Concrete controller classes of every module API layer (framework API layers excluded).</summary>
    internal static IEnumerable<Type> ModuleApiConcreteClasses(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return map.Layers
            .Where(l => l.Layer == Layer.Api && l.Module.Length > 0)
            .SelectMany(l => l.Assembly.ConcreteClasses);
    }

    /// <summary>Whether the method overrides one of the base read actions of <c>EntityControllerBase</c>.</summary>
    private static bool IsBaseReadOverride(MethodInfo method)
    {
        if (!BaseReadActionNames.Contains(method.Name, StringComparer.Ordinal))
            return false;

        var baseDefinition = method.GetBaseDefinition();
        if (baseDefinition == method)
            return false;

        var declaringType = baseDefinition.DeclaringType;
        return declaringType is { IsGenericType: true }
            && string.Equals(declaringType.GetGenericTypeDefinition().FullName, EntityControllerBaseDefinitionFullName, StringComparison.Ordinal);
    }

    /// <summary>Whether the member itself (not a base declaration) carries an attribute with the given full name.</summary>
    private static bool HasAttributeWithFullName(MemberInfo member, string attributeFullName) =>
        member.GetCustomAttributesData()
            .Any(a => string.Equals(a.AttributeType.FullName, attributeFullName, StringComparison.Ordinal));

    /// <summary>Whether the method declares a 412 or 428 through a <c>ProducesResponseTypeAttribute</c> (or a subclass).</summary>
    private static bool DeclaresPreconditionResponse(MethodInfo method) =>
        method.GetCustomAttributes(inherit: false)
            .Where(a => DerivesFromFullName(a.GetType(), ProducesResponseTypeAttributeFullName))
            .Select(a => a.GetType().GetProperty("StatusCode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(a))
            .Any(status => status is 412 or 428);

    /// <summary>Whether the type, or any of its base types, has the given full name.</summary>
    private static bool DerivesFromFullName(Type type, string fullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsController(Type type) =>
        type.SimpleName.EndsWith("Controller", StringComparison.Ordinal)
        || type.HasBaseTypeStartingWith("Microsoft.AspNetCore.Mvc.ControllerBase")
        || type.HasBaseTypeStartingWith("Microsoft.AspNetCore.Mvc.Controller");
}
