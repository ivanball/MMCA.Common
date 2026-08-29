using MMCA.Common.Shared.Conventions;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Shared namespace-derivation conventions used for both SQL schema names and logical data
/// source (database) names, so the two can never drift apart. The parsing itself lives in
/// <see cref="ModuleNameConventions"/> (Shared), so the CQRS logging decorators in Application
/// enrich their scopes with the same module name this layer maps entities by.
/// </summary>
internal static class NamespaceConventions
{
    /// <summary>
    /// Derives the module name from an entity's namespace: the segment preceding <c>Domain</c>.
    /// E.g. <c>MMCA.Store.Sales.Domain.Orders</c> gives <c>"Sales"</c>;
    /// <c>MMCA.Modules.Catalog.Domain</c> gives <c>"Catalog"</c>.
    /// </summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <returns>The module name, or <see langword="null"/> when the namespace has no <c>Domain</c> segment.</returns>
    internal static string? GetModuleName(Type entityType) =>
        ModuleNameConventions.GetModuleName(entityType);
}
