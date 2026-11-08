namespace MMCA.Common.Mcp;

/// <summary>
/// Restricts an MCP tool to callers holding <see cref="Permission"/>, checked through the same
/// <c>perm:</c> authorization policy <c>[HasPermission]</c> uses. A caller without it does not see the
/// tool in <c>tools/list</c> and gets an error result when calling it. A tool without this attribute
/// is public: it needs no token, so it must expose only data an anonymous HTTP endpoint already serves.
/// Mark such a tool <see cref="McpToolPublicAttribute"/> to record the choice.
/// </summary>
/// <param name="permission">The permission name, for example <c>"sessions:manage"</c>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpToolPermissionAttribute(string permission) : Attribute
{
    /// <summary>Gets the permission a caller must hold to see and call the tool.</summary>
    public string Permission { get; } = permission;
}
