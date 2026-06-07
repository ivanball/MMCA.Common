namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Shared namespace-derivation conventions used for both SQL schema names and logical data
/// source (database) names, so the two can never drift apart.
/// </summary>
internal static class NamespaceConventions
{
    /// <summary>
    /// Derives the module name from an entity's namespace: the segment preceding <c>Domain</c>.
    /// E.g. <c>MMCA.Store.Sales.Domain.Orders</c> → <c>"Sales"</c>;
    /// <c>MMCA.Modules.Catalog.Domain</c> → <c>"Catalog"</c>.
    /// </summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <returns>The module name, or <see langword="null"/> when the namespace has no <c>Domain</c> segment.</returns>
    internal static string? GetModuleName(Type entityType)
    {
        var segments = entityType.Namespace?.Split('.') ?? [];
        var domainIndex = Array.FindIndex(segments,
            s => s.Equals("Domain", StringComparison.OrdinalIgnoreCase));
        return domainIndex >= 1 ? segments[domainIndex - 1] : null;
    }
}
