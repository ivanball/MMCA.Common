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
    private const string AuthorizeAttributeFullName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
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

    /// <summary>
    /// Whether <see cref="Endpoints_DeclareAnAuthorizationDecision"/> runs. Off by default so a repo
    /// adopts the stricter gate deliberately; turn it on once every controller and routable page in
    /// <see cref="TargetAssemblies"/> either declares its own decision or appears in
    /// <see cref="EndpointsWithoutAuthorizationAttribute"/>.
    /// </summary>
    /// <remarks>
    /// SECURITY: the allow-list check above can only see endpoints that carry
    /// <c>[AllowAnonymous]</c>, so an endpoint carrying NO authorization attribute at all is
    /// invisible to it: exactly the shape a forgotten <c>[Authorize]</c> produces. On controllers
    /// the framework's fallback authorization policy now closes that hole at run time, but a
    /// routable Blazor component is gated by <c>AuthorizeRouteView</c>, which reads attributes and
    /// ignores the fallback policy entirely, so for pages this test IS the control.
    /// </remarks>
    protected virtual bool RequireExplicitAuthorizationDecision => false;

    /// <summary>
    /// Endpoints deliberately left without an authorization attribute, identified the same way as
    /// <see cref="AllowedAnonymousEndpoints"/>. Every entry is an endpoint whose gate is the
    /// fallback authorization policy rather than an attribute; a routable page belongs here only
    /// when it renders nothing that depends on the caller.
    /// </summary>
    protected virtual IReadOnlyCollection<string> EndpointsWithoutAuthorizationAttribute => [];

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

    [Fact]
    public void Endpoints_DeclareAnAuthorizationDecision()
    {
        if (!RequireExplicitAuthorizationDecision)
        {
            // Repo has not opted into the stricter gate yet. Assert the invariant the two scans
            // share instead, so this is a real check rather than an empty one: an endpoint cannot
            // be both explicitly anonymous and undecorated.
            UndecoratedEndpoints().Should().NotIntersectWith(
                AnonymousEndpoints(),
                "an endpoint carrying [AllowAnonymous] is a declared decision, not an omission");
            return;
        }

        var offenders = UndecoratedEndpoints()
            .Where(endpoint => !EndpointsWithoutAuthorizationAttribute.Contains(endpoint, StringComparer.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "an endpoint carrying neither [Authorize] nor [AllowAnonymous] is invisible to the allow-list gate, and a routable page is anonymous no matter what the fallback authorization policy says; undecorated: {0}",
            string.Join("; ", offenders));
    }

    [Fact]
    public void UndecoratedAllowList_HasNoStaleEntries()
    {
        var undecorated = UndecoratedEndpoints().ToHashSet(StringComparer.Ordinal);

        var stale = EndpointsWithoutAuthorizationAttribute
            .Where(entry => !undecorated.Contains(entry))
            .ToList();

        stale.Should().BeEmpty(
            "an entry that no longer matches an undecorated endpoint hides a rename behind a review that already happened; stale: {0}",
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

    /// <summary>
    /// Every scanned endpoint that declares NO authorization attribute, as allow-list identifiers.
    /// A controller action counts as decided when the action itself or any type in its declaring
    /// hierarchy carries <c>[Authorize]</c> (<c>[HasPermission]</c> derives from it) or
    /// <c>[AllowAnonymous]</c>; a routable component counts as decided when the component type
    /// itself carries one.
    /// </summary>
    /// <returns>The identifiers of every endpoint left to the fallback policy.</returns>
    protected IEnumerable<string> UndecoratedEndpoints() =>
        TargetAssemblies
            .SelectMany(assembly => assembly.LoadableTypes)
            .Where(IsScannedEndpointType)
            .SelectMany(UndecoratedEndpointsOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> UndecoratedEndpointsOf(Type type)
    {
        // Only concrete types are routed: an abstract framework base declares no gate on purpose,
        // and the concrete controller inheriting its actions is where the decision has to be made
        // (and is where this check makes it). Reporting is per type rather than per action for the
        // same reason: a controller that forgot [Authorize] usually declares no method of its own,
        // so the whole inherited action set is what went anonymous.
        if (type.IsAbstract || DeclaresAuthorizationAnywhere(type))
        {
            yield break;
        }

        if (type.FullName is { } typeName)
        {
            yield return typeName;
        }
    }

    // A decision counts wherever ASP.NET Core would find one: on the type, on any type it inherits
    // from (the framework bases declare theirs once), or on any action it exposes. The check is
    // deliberately per type rather than per action: a controller that forgot [Authorize] usually
    // declares nothing at all, and that is the shape worth failing on.
    private static bool DeclaresAuthorizationAnywhere(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (HasAuthorizationAttribute(current.GetCustomAttributes(inherit: false)))
            {
                return true;
            }

            var declaredMethods = current.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            if (declaredMethods.Any(method => HasAuthorizationAttribute(method.GetCustomAttributes(inherit: false))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAuthorizationAttribute(object[] attributes) =>
        attributes.Any(a =>
            IsOrDerivesFrom(a.GetType(), AllowAnonymousAttributeFullName)
            || IsOrDerivesFrom(a.GetType(), AuthorizeAttributeFullName));

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
