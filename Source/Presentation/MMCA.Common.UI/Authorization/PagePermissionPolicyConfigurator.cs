using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Authorization;

/// <summary>
/// Registers one <see cref="PagePermissionPolicy"/> policy per permission that a
/// <see cref="HasPagePermissionAttribute"/> declares on a type in <c>MMCA.Common.UI</c> or in a
/// registered <see cref="IUIModule"/>'s assembly. It runs when the authorization options are first
/// built, after the host has registered its modules, so a host adds a guarded page without any
/// registration call of its own. A policy the host already registered under the same name is kept.
/// </summary>
/// <param name="modules">The UI modules the host registered.</param>
internal sealed class PagePermissionPolicyConfigurator(IEnumerable<IUIModule> modules)
    : IConfigureOptions<AuthorizationOptions>
{
    /// <inheritdoc />
    public void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var permissions = modules
            .Select(module => module.Assembly)
            .Append(typeof(PagePermissionPolicy).Assembly)
            .Distinct()
            .SelectMany(LoadableTypes)
            .SelectMany(type => type.GetCustomAttributes<HasPagePermissionAttribute>(inherit: true))
            .Select(attribute => attribute.Permission)
            .Distinct(StringComparer.Ordinal);

        foreach (var permission in permissions)
        {
            var name = PagePermissionPolicy.NameFor(permission);
            if (options.GetPolicy(name) is null)
            {
                options.AddPolicy(name, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PagePermissionRequirement(permission)));
            }
        }
    }

    // A module assembly that references something the head does not load would otherwise fail the
    // whole scan; the types that did load still declare their pages.
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
