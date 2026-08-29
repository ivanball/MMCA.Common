namespace MMCA.Common.Shared.Conventions;

/// <summary>
/// Derives the owning module of a type from its namespace, following the workspace naming
/// convention <c>MMCA.{App}.{Module}.{Layer}</c> (e.g. <c>MMCA.Store.Sales.Domain.Orders</c>).
/// Lives in Shared because the derivation is needed both by persistence (SQL schema names and
/// logical data-source names) and by the CQRS logging decorators in Application, and Application
/// may not reference Infrastructure.
/// </summary>
public static class ModuleNameConventions
{
    /// <summary>
    /// Layer segments other than <c>Domain</c> that mark the end of a module name in the full
    /// <c>MMCA.{App}.{Module}.{Layer}</c> shape. <c>Shared</c> is deliberately absent: framework
    /// namespaces (<c>MMCA.Common.Shared.*</c>) must not resolve to a phantom module.
    /// </summary>
    private static readonly string[] NonDomainLayerSegments = ["Application", "Infrastructure", "API", "UI"];

    /// <summary>
    /// Derives the module name from a type's namespace: the segment preceding the first layer
    /// segment. E.g. <c>MMCA.Store.Sales.Domain.Orders</c> gives <c>"Sales"</c> and
    /// <c>MMCA.ADC.Conference.Application.Sessions</c> gives <c>"Conference"</c>. Matches are
    /// case-insensitive and the first matching segment wins.
    /// </summary>
    /// <remarks>
    /// <c>Domain</c> matches at any position past the first segment (the original persistence
    /// rule, kept byte-for-byte for schema and data-source naming). The other layer segments
    /// (<c>Application</c>, <c>Infrastructure</c>, <c>API</c>, <c>UI</c>) only match at the
    /// fourth segment or later, the full <c>MMCA.{App}.{Module}.{Layer}</c> shape, so framework
    /// namespaces such as <c>MMCA.Common.Application.*</c> resolve to no module rather than to a
    /// phantom <c>"Common"</c> module.
    /// </remarks>
    /// <param name="type">The CLR type whose namespace carries the convention.</param>
    /// <returns>
    /// The module name, or <see langword="null"/> when the namespace has no qualifying layer
    /// segment, when nothing precedes it, or when the type has no namespace.
    /// </returns>
    public static string? GetModuleName(Type type)
    {
        var segments = type.Namespace?.Split('.') ?? [];
        var domainIndex = Array.FindIndex(segments,
            s => s.Equals("Domain", StringComparison.OrdinalIgnoreCase));
        if (domainIndex >= 1)
        {
            return segments[domainIndex - 1];
        }

        var layerIndex = Array.FindIndex(segments,
            s => Array.Exists(NonDomainLayerSegments, l => l.Equals(s, StringComparison.OrdinalIgnoreCase)));
        return layerIndex >= 3 ? segments[layerIndex - 1] : null;
    }
}
