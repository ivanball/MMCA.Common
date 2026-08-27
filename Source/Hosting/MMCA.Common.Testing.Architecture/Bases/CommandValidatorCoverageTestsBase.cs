namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Command-validation coverage fitness function: the Validating decorator runs FluentValidation
/// before the transaction opens, so a command with no validator carries whatever the caller sent
/// straight into the handler. The pipeline stage is there, it simply has nothing to run, and the gap
/// is invisible until bad input reaches the domain.
/// <para>
/// Every command that carries data (at least one public settable property, <c>init</c> included) and
/// is handled by an <c>ICommandHandler</c> in the repo's per-module Application assemblies must be
/// covered, either by its own <c>IValidator&lt;TCommand&gt;</c> or through the
/// <c>CommandRequestValidator</c> bridge: the command implements
/// <c>ICommandWithRequest&lt;TRequest&gt;</c> AND an <c>IValidator&lt;TRequest&gt;</c> exists. Both
/// halves of the bridge are required, because the framework registers the bridge validator for every
/// such command whether or not a request validator resolves, and one that resolves none adds no rules.
/// </para>
/// <para>
/// Adoption: subclass and supply <see cref="Map"/>. Run once, write the missing validators, and put
/// the genuinely validation-free commands into <see cref="AllowedUnvalidatedCommands"/>.
/// </para>
/// </summary>
public abstract class CommandValidatorCoverageTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Command type full names or namespace prefixes that legitimately need no validation: an
    /// internal maintenance command, or one whose only payload is a server-supplied identifier.
    /// Empty by default, which requires coverage everywhere.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedUnvalidatedCommands => [];

    /// <summary>
    /// Minimum number of commands the scan must find, so a map that resolves to no module Application
    /// assemblies cannot let the gate pass without reading anything. Raise it to the repo's known
    /// command count to also catch a module dropping out of the map.
    /// </summary>
    protected virtual int MinimumCommands => 1;

    [Fact]
    public void Commands_ShouldHave_ValidationCoverage() =>
        ArchitectureRules.CommandsHaveValidators(Map, AllowedUnvalidatedCommands);

    [Fact]
    public void CommandInventory_ShouldNotBe_Empty() =>
        ArchitectureRules.HandledCommandCount(Map).Should().BeGreaterThanOrEqualTo(
            MinimumCommands,
            because: "the coverage rule must actually read a module's commands; finding none means the map registers no module Application assemblies and the gate is vacuous");
}
