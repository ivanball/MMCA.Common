namespace MMCA.Common.Mcp;

/// <summary>
/// Configuration for the MCP endpoint, bound from the <c>Mcp</c> section. The endpoint is off
/// unless <see cref="Enabled"/> is <see langword="true"/>: with it off, <c>AddCommonMcpServer</c>
/// registers no MCP service and <c>MapCommonMcp</c> maps no route, so the path answers 404.
/// </summary>
/// <example>
/// <code>
/// "Mcp": {
///   "Enabled": true,
///   "Tools": {
///     "list_sessions": { "PermitsPerMinute": 30 },
///     "now_next": { "Enabled": false }
///   }
/// }
/// </code>
/// </example>
public sealed class McpSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public const string SectionName = "Mcp";

    /// <summary>Gets or sets a value indicating whether the MCP endpoint is registered and mapped.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets per-tool settings keyed by tool name (the <c>Name</c> of its <c>[McpServerTool]</c>).
    /// A tool with no entry is enabled and limited only by the endpoint's <c>mcp</c> rate-limit policy.
    /// </summary>
    public IDictionary<string, McpToolSettings> Tools { get; } = new Dictionary<string, McpToolSettings>(StringComparer.OrdinalIgnoreCase);
}
