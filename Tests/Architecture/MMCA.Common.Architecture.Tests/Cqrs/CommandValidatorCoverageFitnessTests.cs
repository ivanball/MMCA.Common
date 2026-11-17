using MMCA.Common.Architecture.Tests.CommandValidatorFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Self-test for the command-validation coverage rule shipped in
/// <c>MMCA.Common.Testing.Architecture</c> (<see cref="CommandValidatorCoverageTestsBase"/>). It
/// points a map at THIS assembly, whose <c>CommandValidatorFixtures</c> compile every coverage shape,
/// and pins each behaviour: a command with its own validator passes, one covered only through the
/// CommandRequestValidator bridge passes, a command with neither fails, a bridge command whose request
/// has no validator fails (half a bridge validates nothing), a payload-free marker command is skipped,
/// the allowlist exempts exactly what it names, a request validator shipped in the framework
/// Application assembly counts, the generic delete is exempt by default, and an allowlist entry that
/// exempts nothing is reported as stale.
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
    public void BridgeToAFrameworkRequestValidator_IsCovered()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(ForgotTicketPasswordCommand),
                "the request validator ships in MMCA.Common.Application, which AddApplication() registers, so the bridge resolves at run time");
    }

    [Fact]
    public void GenericDeleteEntityCommand_IsExemptWithoutAnEntry()
    {
        var act = () => ArchitectureRules.CommandsHaveValidators(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                "DeleteEntityCommand",
                "the framework's generic delete carries only the route id and is not module-authored");
    }

    [Fact]
    public void AllowlistEntryNamingNoCommand_IsFlaggedAsStale()
    {
        const string stale = $"{FixtureNamespace}.GhostTicketCommand";

        var act = () => ArchitectureRules.CommandsHaveValidators(_map, [FixtureNamespace, stale]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{stale} is allowlisted but names no handled command",
                "an entry that names nothing is stale and must be removed, or the list only grows");
    }

    [Fact]
    public void AllowlistEntryNamingAValidatedCommand_IsFlaggedAsStale()
    {
        var stale = $"{FixtureNamespace}.{nameof(CreateTicketCommand)}";

        var act = () => ArchitectureRules.CommandsHaveValidators(_map, [FixtureNamespace, stale]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{stale} is allowlisted but every command it names is validated or exempt",
                "an entry for a command that has a validator exempts nothing, and would hide the validator's deletion");
    }

    [Fact]
    public void AllowlistEntryForTheExemptGenericDelete_IsFlaggedAsStale()
    {
        const string stale = "MMCA.Common.Application.UseCases.Crud.DeleteEntityCommand";

        var act = () => ArchitectureRules.CommandsHaveValidators(_map, [FixtureNamespace, stale]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{stale} is allowlisted but every command it names is validated or exempt",
                "the built-in exemption makes a consumer's hand entry for the generic delete redundant");
    }

    [Fact]
    public void CommandInventory_CountsTheDataCarryingCommands()
    {
        var count = ArchitectureRules.HandledCommandCount(_map);

        count.Should().Be(
            7,
            "the fixtures compile seven data-carrying commands with handlers (Create, Update, Reopen, Archive, Purge, ForgotTicketPassword and the closed generic delete); the payload-free marker is not one");
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
