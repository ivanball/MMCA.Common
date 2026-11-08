namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // Matched by full name, like the permission coverage rule, because this package takes no framework
    // reference and no reference on the MCP SDK.
    private const string McpServerToolAttributeFullName = "ModelContextProtocol.Server.McpServerToolAttribute";
    private const string McpToolPublicAttributeFullName = "MMCA.Common.Mcp.McpToolPublicAttribute";

    /// <summary>
    /// Every method declared an MCP tool (<c>[McpServerTool]</c>) says who may call it: exactly one of
    /// <c>[McpToolPermission]</c> or <c>[McpToolPublic]</c>. A tool with neither is public by default, a
    /// choice nobody has to make out loud today; with both, the permission gate wins silently and the
    /// public mark is a lie. <c>[McpServerTool]</c> is read with inheritance, so an override of a tool
    /// is a tool; the two declarations are read only where declared, so that override states its own.
    /// </summary>
    /// <param name="assemblies">The assemblies whose MCP tool methods are scanned.</param>
    public static void McpToolsDeclareAccess(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var violations = McpToolMethods(assemblies)
            .Select(tool => (Tool: tool, Declarations: tool.Method.GetCustomAttributes(inherit: false)
                .Select(attribute => attribute.GetType().FullName)
                .Count(name => string.Equals(name, McpToolPermissionAttributeFullName, StringComparison.Ordinal)
                    || string.Equals(name, McpToolPublicAttributeFullName, StringComparison.Ordinal))))
            .Where(result => result.Declarations != 1)
            .Select(result => result.Declarations == 0
                ? $"  - {result.Tool.Location} declares neither [McpToolPermission] nor [McpToolPublic]"
                : $"  - {result.Tool.Location} declares both [McpToolPermission] and [McpToolPublic]")
            .Order(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(violations,
            "every MCP tool must declare who may call it: exactly one of [McpToolPermission] or [McpToolPublic]");
    }

    /// <summary>
    /// Counts the MCP tool methods <see cref="McpToolsDeclareAccess"/> sees: a non-vacuity floor, so a
    /// renamed assembly cannot let the rule pass without having inspected anything.
    /// </summary>
    /// <param name="assemblies">The assemblies whose MCP tool methods are scanned.</param>
    /// <returns>The number of MCP tool methods found.</returns>
    public static int CountMcpTools(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return McpToolMethods(assemblies).Count();
    }

    private static IEnumerable<(MethodInfo Method, string Location)> McpToolMethods(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags declaredMethods = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assemblies.SelectMany(assembly => assembly.LoadableTypes))
        {
            var typeName = type.FullName ?? type.Name;
            foreach (var method in type.GetMethods(declaredMethods))
            {
                if (method.GetCustomAttributes(inherit: true)
                    .Any(attribute => string.Equals(attribute.GetType().FullName, McpServerToolAttributeFullName, StringComparison.Ordinal)))
                {
                    yield return (method, $"{typeName}.{method.Name}");
                }
            }
        }
    }
}
