using MMCA.Common.Architecture.Tests.CommandValidatorFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Self-test for the command-validation coverage rule shipped in
/// <c>MMCA.Common.Testing.Architecture</c> (<see cref="CommandValidatorCoverageTestsBase"/>). It
/// points a map at THIS assembly, whose <c>CommandValidatorFixtures</c> compile every coverage shape,
/// and pins each behaviour: a command with its own validator passes, one covered only through the
/// CommandRequestValidator bridge passes, a command with neither fails, a bridge command whose request
/// has no validator fails (half a bridge validates nothing), a payload-free marker command is skipped,
/// and the allowlist exempts exactly what it names.
/// </summary>
public sealed class CommandValidatorCoverageFitnessTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.CommandValidatorFixtures";

    private readonly FixtureModuleMap _map = new();

    [Fact]
    public void CommandWithNoValidation_IsFlagged()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(ArchiveTicketCommand),
                "a command that carries data and has a handler must be validated before the handler runs");
    }

    [Fact]
    public void CommandWithItsOwnValidator_IsCovered()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(CreateTicketCommand),
                "an explicit IValidator<TCommand> is direct coverage");
    }

    [Fact]
    public void CommandCoveredThroughTheRequestBridge_IsCovered()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(UpdateTicketCommand),
                "ICommandWithRequest<T> plus an IValidator<T> is what CommandRequestValidator bridges into command validation");
    }

    [Fact]
    public void BridgeWithNoRequestValidator_IsNotCoverage()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(ReopenTicketCommand),
                "CommandRequestValidator is registered for every bridged command but adds no rule when no request validator resolves");
    }

    [Fact]
    public void PayloadFreeCommand_IsSkipped()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(RebuildTicketIndexCommand),
                "a marker command with no settable property has nothing to validate");
    }

    [Fact]
    public void AllowlistedCommand_IsExempt()
    {
        var allowed = $"{FixtureNamespace}.{nameof(PurgeTicketsCommand)}";

        var act = () => ArchitectureRules.CommandsHaveValidators(_map, [allowed]);

        var message = act.Should().Throw<XunitException>(
            "the other uncovered fixtures are still outside the allowlist").Which.Message;

        message.Should().NotContain(nameof(PurgeTicketsCommand));
        message.Should().Contain(nameof(ArchiveTicketCommand));
    }

    [Fact]
    public void AllowlistedNamespace_SilencesTheRule()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, [FixtureNamespace]);

        act.Should().NotThrow(
            "a namespace entry covers the commands under it, which is how a repo records the ones that need no validation");
    }

    [Fact]
    public void CommandInventory_CountsTheDataCarryingCommands()
    {
        var count = ArchitectureRules.HandledCommandCount(_map);

        count.Should().Be(
            5,
            "the fixtures compile five data-carrying commands with handlers (Create, Update, Reopen, Archive, Purge); the payload-free marker is not one");
    }

    /// <summary>
    /// A map registering this test assembly as a MODULE Application layer: the rule reads commands and
    /// validators from the per-module Application assemblies, mirroring where the framework's
    /// AddValidatorsFromAssembly scan looks.
    /// </summary>
    private sealed class FixtureModuleMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Tickets", Layer.Application, typeof(CreateTicketCommand).Assembly),
        ];
    }
}
