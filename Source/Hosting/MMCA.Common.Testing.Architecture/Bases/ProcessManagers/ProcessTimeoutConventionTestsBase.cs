namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Process timeout conventions (the process-manager primitive): every concrete
/// <c>IProcessTimeoutCommand&lt;T&gt;</c> in the repo's module Application assemblies declares a stable
/// <c>[InternalCommandName]</c>, and is handled by exactly one <c>ProcessTimeoutHandlerBase</c>
/// subclass.
/// <para>
/// A timeout command is a durable row that may sit in the queue across a deploy, so a rename without
/// a stable name strands it, and a timeout with no handler (or two) cannot run as intended.
/// </para>
/// <para>
/// Adoption: subclass and supply <see cref="Map"/>, only in a repo that uses the primitive: the scan must
/// find at least <see cref="MinimumTimeoutCommands"/> timeout commands, so the two rules cannot pass
/// without reading anything.
/// </para>
/// </summary>
public abstract class ProcessTimeoutConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Minimum number of process timeout commands the scan must find, so a map that omits the module
    /// declaring them (or a command that loses its <c>IProcessTimeoutCommand&lt;T&gt;</c> in a refactor)
    /// cannot let both rules pass vacuously. Raise it to the repo's known count to also catch one
    /// module dropping out of the map.
    /// </summary>
    protected virtual int MinimumTimeoutCommands => 1;

    [Fact]
    public void ProcessTimeoutCommands_AreDiscovered() =>
        ArchitectureRules.ProcessTimeoutCommandCount(Map).Should().BeGreaterThanOrEqualTo(
            MinimumTimeoutCommands,
            because: "the timeout conventions must actually read the repo's timeout commands; finding fewer means the map omits a module Application assembly and both rules pass without reading anything");

    [Fact]
    public void ProcessTimeoutCommands_DeclareAStableInternalCommandName() =>
        ArchitectureRules.ProcessTimeoutCommandsDeclareAStableInternalCommandName(Map);

    [Fact]
    public void ProcessTimeoutCommands_HaveExactlyOneTimeoutHandler() =>
        ArchitectureRules.ProcessTimeoutCommandsHaveExactlyOneTimeoutHandler(Map);
}
