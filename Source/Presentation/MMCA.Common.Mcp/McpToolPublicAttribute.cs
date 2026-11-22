namespace MMCA.Common.Mcp;

/// <summary>
/// Declares that an MCP tool is intentionally public: it needs no token, so it must expose only data
/// an anonymous HTTP endpoint already serves. A tool without <see cref="McpToolPermissionAttribute"/>
/// is public, and this attribute records that the choice was made on purpose: <c>MapCommonMcp</c>
/// refuses to start when a registered tool carries neither, and
/// <c>McpToolAccessDeclarationTestsBase</c> requires every <c>[McpServerTool]</c> method to carry
/// exactly one of the two.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpToolPublicAttribute : Attribute;
