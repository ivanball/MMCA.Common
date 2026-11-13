using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.OutputCaching;
using MMCA.Common.API.Controllers;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Marks the inherited base reads of an entity controller (both <c>GetAllAsync</c> overloads,
/// <c>GetAllForLookupAsync</c> and <c>GetByIdAsync</c> of <see cref="EntityControllerBase{TEntity, TEntityDTO, TIdentifierType}"/>)
/// as anonymous and output-cached under one named policy, and names the by-id route, so a controller
/// no longer overrides a base read only to add attributes.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it adds.</b> On each selector of a base read it appends <see cref="AllowAnonymousAttribute"/>
/// and <c>[OutputCache(PolicyName = ...)]</c> to the endpoint metadata, each only when the selector
/// does not already carry one (an override's own <c>[OutputCache]</c> wins, whatever its policy). The
/// by-id read gets the route name <see cref="ByIdRouteName"/>, or <c>Get{EntityName}ById</c> when that is
/// null, unless an override already declared a name. Nothing is added to the action filters, so the
/// API description is unchanged apart from the operationId a new route name produces.
/// </para>
/// <para>
/// <b>What it leaves alone.</b> The export, every write, the opt-in cursor and change-feed reads, and
/// every action the application declares itself keep exactly the authorization and caching the
/// controller gives them. A controller without the attribute is untouched.
/// </para>
/// <para>
/// <b>What it refuses.</b> An override of a base read is still a base read and is marked like an
/// inherited one. If that override declares its own <c>[Authorize]</c> or permission attribute and no
/// <c>[AllowAnonymous]</c>, the convention throws when the application model is built, because the
/// anonymous marker would silently replace the requirement. A read that needs a gate belongs on a
/// controller without this attribute.
/// </para>
/// <para>
/// The policy must be registered with <c>AddOutputCache</c> (for example through
/// <c>AddPublicEndpointPolicy</c>); an unknown name fails at request time exactly as
/// <c>[OutputCache(PolicyName = ...)]</c> does.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PublicCachedReadsAttribute : Attribute, IControllerModelConvention
{
    /// <summary>The base read actions the attribute marks; both <c>GetAllAsync</c> overloads share a name.</summary>
    private static readonly string[] ReadActionNames =
    [
        nameof(EntityControllerBase<,,>.GetAllAsync),
        nameof(EntityControllerBase<,,>.GetAllForLookupAsync),
        nameof(EntityControllerBase<,,>.GetByIdAsync),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicCachedReadsAttribute"/> class.
    /// </summary>
    /// <param name="policyName">The output-cache policy the base reads are cached under.</param>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is null, empty or whitespace.</exception>
    public PublicCachedReadsAttribute(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        PolicyName = policyName;
    }

    /// <summary>Gets the output-cache policy the base reads are cached under.</summary>
    public string PolicyName { get; }

    /// <summary>
    /// Gets the route name given to the by-id read when no override declared one. Null (the default)
    /// derives <c>Get{EntityName}ById</c> from the controller's entity type; empty or whitespace is
    /// rejected when the convention runs.
    /// </summary>
    public string? ByIdRouteName { get; init; }

    /// <summary>
    /// Adds the anonymous and output-cache endpoint metadata to every base read of the controller and
    /// names its by-id route.
    /// </summary>
    /// <param name="controller">The controller carrying the attribute.</param>
    /// <exception cref="InvalidOperationException">
    /// The controller does not derive from <see cref="EntityControllerBase{TEntity, TEntityDTO, TIdentifierType}"/>,
    /// <see cref="ByIdRouteName"/> is empty or whitespace, or an overridden base read declares its own
    /// authorization attribute without <c>[AllowAnonymous]</c>.
    /// </exception>
    public void Apply(ControllerModel controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        var entityControllerBase = FindEntityControllerBase(controller.ControllerType)
            ?? throw new InvalidOperationException(
                $"[{nameof(PublicCachedReadsAttribute)}] is on {controller.ControllerType.FullName}, which does not derive from EntityControllerBase<TEntity, TEntityDTO, TIdentifierType>; it marks only the base reads of an entity controller.");

        if (ByIdRouteName is not null && string.IsNullOrWhiteSpace(ByIdRouteName))
        {
            throw new InvalidOperationException(
                $"[{nameof(PublicCachedReadsAttribute)}] on {controller.ControllerType.FullName} has an empty {nameof(ByIdRouteName)}; leave it null to derive Get{{EntityName}}ById.");
        }

        var byIdRouteName = ByIdRouteName ?? $"Get{entityControllerBase.GetGenericArguments()[0].Name}ById";

        foreach (var action in controller.Actions.Where(IsBaseRead))
        {
            EnsureNoDeclaredAuthorization(controller, action);

            var isById = string.Equals(action.ActionMethod.Name, nameof(EntityControllerBase<,,>.GetByIdAsync), StringComparison.Ordinal);

            foreach (var selector in action.Selectors)
            {
                MarkSelector(selector, isById ? byIdRouteName : null);
            }
        }
    }

    /// <summary>
    /// Appends the anonymous and output-cache metadata to one selector of a base read, each only when
    /// absent, and names the route when <paramref name="routeName"/> is given and none is declared.
    /// </summary>
    /// <param name="selector">A selector of a base read action.</param>
    /// <param name="routeName">The by-id route name, or null for a read that is not the by-id read.</param>
    private void MarkSelector(SelectorModel selector, string? routeName)
    {
        if (!selector.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            selector.EndpointMetadata.Add(new AllowAnonymousAttribute());
        }

        if (!selector.EndpointMetadata.OfType<OutputCacheAttribute>().Any())
        {
            selector.EndpointMetadata.Add(new OutputCacheAttribute { PolicyName = PolicyName });
        }

        if (routeName is not null && selector.AttributeRouteModel is { Name: null } routeModel)
        {
            routeModel.Name = routeName;
        }
    }

    /// <summary>
    /// Refuses a base read whose own method declares authorization. The attribute adds anonymous access
    /// to every base read, and anonymous access wins over <c>[Authorize]</c> or a permission attribute,
    /// so an override that declares one would become public without anyone having said so. An override
    /// that also declares <c>[AllowAnonymous]</c> has said so and is left alone, and so is authorization
    /// declared on the controller class (the usual shape: gated writes, public reads).
    /// </summary>
    /// <param name="controller">The controller carrying the attribute.</param>
    /// <param name="action">A base read of that controller.</param>
    /// <exception cref="InvalidOperationException">The read declares authorization and no <c>[AllowAnonymous]</c>.</exception>
    private static void EnsureNoDeclaredAuthorization(ControllerModel controller, ActionModel action)
    {
        if (action.Attributes.OfType<IAuthorizeData>().Any() && !action.Attributes.OfType<IAllowAnonymous>().Any())
        {
            throw new InvalidOperationException(
                $"[{nameof(PublicCachedReadsAttribute)}] is on {controller.ControllerType.FullName}, whose {action.ActionMethod.Name} declares its own authorization attribute. The attribute marks every base read anonymous and output-cached, which would silently replace that requirement. Remove the authorization attribute from the read, add [AllowAnonymous] to it to state that the read is public, or move the gated read to a controller without [{nameof(PublicCachedReadsAttribute)}].");
        }
    }

    /// <summary>The closed <c>EntityControllerBase&lt;,,&gt;</c> in the type's base chain, or null.</summary>
    private static Type? FindEntityControllerBase(Type controllerType)
    {
        for (var current = controllerType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(EntityControllerBase<,,>))
                return current;
        }

        return null;
    }

    /// <summary>
    /// Whether the action is one of the base reads, inherited or overridden: its base definition is
    /// declared on a closed <c>EntityControllerBase&lt;,,&gt;</c> and its name is a read action name.
    /// </summary>
    private static bool IsBaseRead(ActionModel action)
    {
        var declaringType = action.ActionMethod.GetBaseDefinition().DeclaringType;

        return declaringType is { IsGenericType: true }
            && declaringType.GetGenericTypeDefinition() == typeof(EntityControllerBase<,,>)
            && ReadActionNames.Contains(action.ActionMethod.Name, StringComparer.Ordinal);
    }
}
