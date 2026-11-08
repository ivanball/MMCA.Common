namespace MMCA.Common.Mcp;

/// <summary>
/// Declares that an MCP tool is intentionally public: it needs no token, so it must expose only data
/// an anonymous HTTP endpoint already serves. Purely a declaration, with no runtime effect: a tool
/// without <see cref="McpToolPermissionAttribute"/> is public either way, and this attribute records
/// that the choice was made on purpose. <c>McpToolAccessDeclarationTestsBase</c> requires every
/// <c>[McpServerTool]</c> method to carry exactly one of the two.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpToolPublicAttribute : Attribute;
