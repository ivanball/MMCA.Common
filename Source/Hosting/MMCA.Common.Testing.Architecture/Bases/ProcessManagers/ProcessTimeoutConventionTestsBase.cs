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
/// Adoption: subclass and supply <see cref="Map"/>.
/// </para>
/// </summary>
public abstract class ProcessTimeoutConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void ProcessTimeoutCommands_DeclareAStableInternalCommandName() =>
        ArchitectureRules.ProcessTimeoutCommandsDeclareAStableInternalCommandName(Map);

    [Fact]
    public void ProcessTimeoutCommands_HaveExactlyOneTimeoutHandler() =>
        ArchitectureRules.ProcessTimeoutCommandsHaveExactlyOneTimeoutHandler(Map);
}
