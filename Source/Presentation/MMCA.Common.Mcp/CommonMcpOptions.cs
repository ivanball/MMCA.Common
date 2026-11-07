using System.Reflection;

namespace MMCA.Common.Mcp;

/// <summary>
/// The tools <c>AddCommonMcpServer</c> exposes: every <c>[McpServerToolType]</c> class in
/// <see cref="ToolAssemblies"/>, plus each class in <see cref="ToolTypes"/>.
/// </summary>
public sealed class CommonMcpOptions
{
    /// <summary>Gets the assemblies scanned for <c>[McpServerToolType]</c> classes (typically a module's API assembly).</summary>
    public IList<Assembly> ToolAssemblies { get; } = [];

    /// <summary>Gets individual tool classes to expose, for a host that does not want a whole assembly scanned.</summary>
    public IList<Type> ToolTypes { get; } = [];
}
