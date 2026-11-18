using System.Reflection;
using MMCA.Common.API.Caching;

namespace MMCA.Common.Testing.Support;

/// <summary>
/// Reads the output-cache tags an action evicts declaratively: every <see cref="EvictOutputCacheAttribute"/>
/// on the action's controller and on the action method itself, the two places MVC picks the result filter
/// up from. A controller test pins an action's eviction set with
/// <c>EvictOutputCacheAssertions.EvictedTagsOf&lt;CategoriesController&gt;(nameof(CategoriesController.RenameAsync))</c>;
/// the filter itself (it evicts only after a 2xx write, best effort and under
/// <see cref="CancellationToken.None"/>) is pinned by MMCA.Common's <c>EvictOutputCacheAttributeTests</c>.
/// </summary>
public static class EvictOutputCacheAssertions
{
    /// <summary>Returns the distinct tags the named action of <typeparamref name="TController"/> evicts, in ordinal order.</summary>
    /// <typeparam name="TController">The controller declaring the action.</typeparam>
    /// <param name="actionName">The action method name (every overload of that name is read).</param>
    /// <returns>The evicted tags.</returns>
    /// <exception cref="InvalidOperationException">The controller declares no public action of that name.</exception>
    public static string[] EvictedTagsOf<TController>(string actionName)
    {
        var actions = typeof(TController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
            .ToArray();
        if (actions.Length == 0)
        {
            throw new InvalidOperationException($"{typeof(TController).Name} declares no action named {actionName}.");
        }

        return [.. typeof(TController).GetCustomAttributes<EvictOutputCacheAttribute>(inherit: true)
            .Concat(actions.SelectMany(a => a.GetCustomAttributes<EvictOutputCacheAttribute>(inherit: true)))
            .SelectMany(a => a.Tags)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }
}
