namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// MCP tool access declaration fitness function: every <c>[McpServerTool]</c> method carries exactly one
/// of <c>[McpToolPermission]</c> or <c>[McpToolPublic]</c>, so no tool is public by accident. Authored
/// once here and re-run as a thin subclass per repo: the subclass supplies the
/// <see cref="TargetAssemblies"/> holding its tools. See
/// <see cref="ArchitectureRules.McpToolsDeclareAccess"/>.
/// </summary>
public abstract class McpToolAccessDeclarationTestsBase
{
    /// <summary>The assemblies whose MCP tool methods are scanned.</summary>
    protected abstract IReadOnlyCollection<Assembly> TargetAssemblies { get; }

    /// <summary>
    /// Minimum number of MCP tools the scan must find: a non-vacuity guard, so a renamed assembly cannot
    /// let the check pass without inspecting anything. Override to the repo's known floor.
    /// </summary>
    protected virtual int MinimumMcpTools => 1;

    [Fact]
    public void EveryMcpTool_ShouldDeclare_WhoMayCallIt() =>
        FluentActions.Invoking(() => ArchitectureRules.McpToolsDeclareAccess(TargetAssemblies))
            .Should().NotThrow();

    [Fact]
    public void ScannedMcpToolSet_IsNotEmpty() =>
        ArchitectureRules.CountMcpTools(TargetAssemblies).Should().BeGreaterThanOrEqualTo(
            MinimumMcpTools,
            "the MCP tools must actually be inspected (expected at least {0})",
            MinimumMcpTools);
}
