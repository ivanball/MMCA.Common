namespace MMCA.Common.Mcp;

/// <summary>Settings for one MCP tool, bound from <c>Mcp:Tools:&lt;name&gt;</c>.</summary>
public sealed class McpToolSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the tool is offered. A disabled tool is absent from
    /// <c>tools/list</c> and a call to it returns an error result. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the calls per minute one caller (user id, else client IP) may make to this tool,
    /// counted in memory per instance. The call after the limit returns an error result.
    /// <see langword="null"/> (the default) sets no per-tool limit.
    /// </summary>
    public int? PermitsPerMinute { get; set; }
}
