namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Anonymous-endpoint fitness function: every <c>[AllowAnonymous]</c> in the scanned assemblies must
/// appear in an explicit allow-list, so a new one is a deliberate, reviewed line in a test file
/// rather than an attribute nobody notices. Authored once here and re-run as a thin subclass per
/// repo: the subclass supplies its <see cref="TargetAssemblies"/>, its
/// <see cref="AllowedAnonymousEndpoints"/>, and a <see cref="MinimumScannedTypes"/> non-vacuity
/// floor.
/// <para>
/// Two shapes are scanned: MVC controllers (types deriving from <c>ControllerBase</c>, abstract
/// bases included, because that is where a framework action declares the attribute the derived
/// controller routes) and routable Blazor components (types carrying <c>RouteAttribute</c>).
/// Attributes are read with <c>DeclaredOnly</c> and without inheritance, so one framework base
/// action is reported once at its declaration site instead of once per derived controller in every
/// consumer.
/// </para>
/// <para>
/// Known limitation: minimal-API endpoints opt out of authorization through the
/// <c>.AllowAnonymous()</c> builder call, which produces endpoint metadata at map time and is
/// invisible to static reflection. The framework's own minimal-API anonymous surface is deliberate
/// and small (JWKS, OIDC discovery, app-association, session-cookie refresh, health) but this base
/// cannot see it; an endpoint-metadata check over a built host would be needed for that.
/// </para>
/// <para>
/// <c>ControllerBase</c>, <c>RouteAttribute</c> and <c>AllowAnonymousAttribute</c> are matched by
/// full-name reflection, keeping this package free of ASP.NET references (see the csproj note).
/// </para>
/// </summary>
public abstract class AnonymousEndpointTestsBase
{
    private const string AllowAnonymousAttributeFullName = "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute";
    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string RouteAttributeFullName = "Microsoft.AspNetCore.Components.RouteAttribute";

    /// <summary>The assemblies whose controllers and routable components are scanned.</summary>
    protected abstract IReadOnlyCollection<Assembly> TargetAssemblies { get; }

    /// <summary>
    /// The endpoints allowed to be anonymous. A type-level attribute is identified by the type's
    /// <c>FullName</c>; a method-level attribute by the declaring type's <c>FullName</c>, a dot, and
    /// the method name (e.g. <c>Some.Namespace.AuthController.LoginAsync</c>).
    /// </summary>
    protected abstract IReadOnlyCollection<string> AllowedAnonymousEndpoints { get; }

    /// <summary>
    /// Minimum number of controller and routable-component types the scan must discover: a
    /// non-vacuity guard, so a renamed assembly or a moved base type cannot let the allow-list check
    /// pass without inspecting anything. Override to the repo's known count.
    /// </summary>
    protected virtual int MinimumScannedTypes => 1;

    [Fact]
    public void AnonymousEndpoints_AreAllowListed()
    {
        var offenders = AnonymousEndpoints()
            .Where(endpoint => !AllowedAnonymousEndpoints.Contains(endpoint, StringComparer.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "every [AllowAnonymous] must be a reviewed entry in AllowedAnonymousEndpoints, so an endpoint cannot lose its authorization gate unnoticed; unlisted: {0}",
            string.Join("; ", offenders));
    }

    [Fact]
    public void ScannedEndpointSet_IsNotEmpty()
    {
        // Guard the guard: with no controllers and no routable components discovered, the allow-list
        // assertion above passes without having looked at anything.
        var count = TargetAssemblies.SelectMany(a => a.LoadableTypes).Count(IsScannedEndpointType);

        count.Should().BeGreaterThanOrEqualTo(
            MinimumScannedTypes,
            "the endpoint set must actually be inspected (expected at least {0} controller or routable component type(s))",
            MinimumScannedTypes);
    }

    [Fact]
    public void AllowList_HasNoStaleEntries()
    {
        var anonymous = AnonymousEndpoints().ToHashSet(StringComparer.Ordinal);

        var stale = AllowedAnonymousEndpoints
            .Where(entry => !anonymous.Contains(entry))
            .ToList();

        stale.Should().BeEmpty(
            "an allow-list entry that no longer matches an [AllowAnonymous] hides a renamed or re-gated endpoint behind a permission that is no longer being granted; stale: {0}",
            string.Join("; ", stale));
    }

    /// <summary>Whether the type is one of the shapes this fitness function inspects.</summary>
    /// <param name="type">A type from one of the target assemblies.</param>
    /// <returns><see langword="true"/> for MVC controllers and routable Blazor components.</returns>
    protected static bool IsScannedEndpointType(Type type) =>
        IsController(type) || IsRoutableComponent(type);

    /// <summary>Whether the type derives from ASP.NET Core's <c>ControllerBase</c>.</summary>
    /// <param name="type">A type from one of the target assemblies.</param>
    /// <returns><see langword="true"/> when any base type is <c>ControllerBase</c>.</returns>
    protected static bool IsController(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, ControllerBaseFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the type is a routable Blazor component (carries <c>RouteAttribute</c>).</summary>
    /// <param name="type">A type from one of the target assemblies.</param>
    /// <returns><see langword="true"/> when the type declares a route.</returns>
    protected static bool IsRoutableComponent(Type type) =>
        type.GetCustomAttributes(inherit: false).Any(a => IsOrDerivesFrom(a.GetType(), RouteAttributeFullName));

    /// <summary>
    /// Every anonymous endpoint the scan found, as allow-list identifiers. Exposed so a subclass can
    /// build a richer report or a repo-specific extra assertion on the same data.
    /// </summary>
    /// <returns>The identifiers of every type- and method-level <c>[AllowAnonymous]</c>.</returns>
    protected IEnumerable<string> AnonymousEndpoints() =>
        TargetAssemblies
            .SelectMany(assembly => assembly.LoadableTypes)
            .Where(IsScannedEndpointType)
            .SelectMany(AnonymousEndpointsOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> AnonymousEndpointsOf(Type type)
    {
        if (HasAllowAnonymous(type.GetCustomAttributes(inherit: false)) && type.FullName is { } typeName)
        {
            yield return typeName;
        }

        // DeclaredOnly plus inherit:false keeps a framework base action reported once, at the base
        // that declares it, instead of once per derived controller in every consumer repo.
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            if (HasAllowAnonymous(method.GetCustomAttributes(inherit: false))
                && method.DeclaringType?.FullName is { } declaringName)
            {
                yield return $"{declaringName}.{method.Name}";
            }
        }
    }

    private static bool HasAllowAnonymous(object[] attributes) =>
        attributes.Any(a => IsOrDerivesFrom(a.GetType(), AllowAnonymousAttributeFullName));

    private static bool IsOrDerivesFrom(Type attributeType, string fullName)
    {
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
