using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.ProcessManagers;
using MMCA.Common.Domain.ProcessManagers;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.ProcessManagers;

/// <summary>
/// Self-test for <see cref="ProcessTimeoutConventionTestsBase"/> (design item 36, rule 3 and 36(a)). It
/// points a map at THIS assembly, where the fixtures below compile every shape: a conforming timeout
/// command (stable name, exactly one handler), a nameless one, one with two handlers and one with none.
/// Every fixture shares this one assembly, so each rule fails on the deliberate offenders; the
/// conforming fixture is proven by never being named in either failure. The subclass is private so
/// xUnit does not collect its inherited (deliberately failing) facts as tests of their own.
/// </summary>
public sealed class ProcessTimeoutConventionFixtureTests
{
    [Fact]
    public void UnnamedTimeoutCommand_IsReported()
    {
        var assert = new FixtureConventionTests().ProcessTimeoutCommands_DeclareAStableInternalCommandName;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(
            nameof(NamelessFixtureTimeout),
            "a stored row resolves its handler by name, so a timeout command without [InternalCommandName] strands its rows on a rename");
        message.Should().NotContain(nameof(ConformingFixtureTimeout));
        message.Should().NotContain(nameof(DoublyHandledFixtureTimeout), "it carries a name; its fault is the other rule's");
        message.Should().NotContain(nameof(UnhandledFixtureTimeout), "it carries a name; its fault is the other rule's");
    }

    [Fact]
    public void TimeoutWithTwoHandlers_IsReported()
    {
        var assert = new FixtureConventionTests().ProcessTimeoutCommands_HaveExactlyOneTimeoutHandler;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(
            nameof(DoublyHandledFixtureTimeout),
            "two handlers for one timeout make the processor's resolution ambiguous");
        message.Should().NotContain(nameof(ConformingFixtureTimeout));
        message.Should().NotContain(nameof(NamelessFixtureTimeout), "it has exactly one handler; its fault is the other rule's");
    }

    [Fact]
    public void TimeoutWithNoHandler_IsReported()
    {
        var assert = new FixtureConventionTests().ProcessTimeoutCommands_HaveExactlyOneTimeoutHandler;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(
            nameof(UnhandledFixtureTimeout),
            "exactly one means a timeout command nothing handles is reported too: its rows would dead-letter");
    }

    [Fact]
    public void ConformingFixture_Passes()
    {
        var conventions = new FixtureConventionTests();
        var naming = conventions.ProcessTimeoutCommands_DeclareAStableInternalCommandName;
        var handlers = conventions.ProcessTimeoutCommands_HaveExactlyOneTimeoutHandler;

        naming.Should().Throw<Exception>("the deliberate offenders share this assembly")
            .Which.Message.Should().NotContain(
                nameof(ConformingFixtureTimeout),
                "a named command whose ProcessId is an explicit interface implementation conforms");
        handlers.Should().Throw<Exception>("the deliberate offenders share this assembly")
            .Which.Message.Should().NotContain(
                nameof(ConformingFixtureTimeout),
                "one ProcessTimeoutHandlerBase subclass is exactly one handler");
    }

    /// <summary>
    /// A map registering this test assembly as a module Application layer, where a consumer's timeout
    /// commands and their handlers live.
    /// </summary>
    private sealed class FixtureMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Fixtures", Layer.Application, typeof(ConformingFixtureTimeout).Assembly),
        ];
    }

    private sealed class FixtureConventionTests : ProcessTimeoutConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap();
    }
}

// -- Fixtures. Commands are classes with get-only properties (no public setter), so the command
// -- validator coverage self-test, which maps this same assembly, skips them as payload-free. Handlers
// -- take no constructor arguments, so the constructor-dependency self-test is unaffected. Nothing here
// -- throws or reads the clock, so the domain-throw and clock-read self-tests are unaffected too.

/// <summary>The process every fixture handler times out.</summary>
internal sealed class FixtureProcess : ProcessManagerAggregateRoot<int>;

/// <summary>Conforming: a stable name, ProcessId as an explicit implementation, exactly one handler.</summary>
[InternalCommandName("Fixtures.ConformingTimeout")]
internal sealed class ConformingFixtureTimeout(int fixtureId) : IProcessTimeoutCommand<int>
{
    public int FixtureId { get; } = fixtureId;

    int IProcessTimeoutCommand<int>.ProcessId => FixtureId;
}

internal sealed class ConformingFixtureTimeoutHandler()
    : ProcessTimeoutHandlerBase<ConformingFixtureTimeout, FixtureProcess, int>(null!, null!, TimeProvider.System)
{
    protected override ProcessTimeout ResolveTimeout(ConformingFixtureTimeout command) =>
        new("FixtureExpiry", TimeSpan.FromMinutes(5));
}

/// <summary>Offender: no [InternalCommandName], so its stored rows depend on the CLR type name.</summary>
internal sealed class NamelessFixtureTimeout(int processId) : IProcessTimeoutCommand<int>
{
    public int ProcessId { get; } = processId;
}

internal sealed class NamelessFixtureTimeoutHandler()
    : ProcessTimeoutHandlerBase<NamelessFixtureTimeout, FixtureProcess, int>(null!, null!, TimeProvider.System)
{
    protected override ProcessTimeout ResolveTimeout(NamelessFixtureTimeout command) =>
        new("FixtureExpiry", TimeSpan.FromMinutes(5));
}

/// <summary>Offender: named, but handled twice.</summary>
[InternalCommandName("Fixtures.DoublyHandledTimeout")]
internal sealed class DoublyHandledFixtureTimeout(int processId) : IProcessTimeoutCommand<int>
{
    public int ProcessId { get; } = processId;
}

internal sealed class FirstDoublyHandledFixtureTimeoutHandler()
    : ProcessTimeoutHandlerBase<DoublyHandledFixtureTimeout, FixtureProcess, int>(null!, null!, TimeProvider.System)
{
    protected override ProcessTimeout ResolveTimeout(DoublyHandledFixtureTimeout command) =>
        new("FixtureExpiry", TimeSpan.FromMinutes(5));
}

internal sealed class SecondDoublyHandledFixtureTimeoutHandler()
    : ProcessTimeoutHandlerBase<DoublyHandledFixtureTimeout, FixtureProcess, int>(null!, null!, TimeProvider.System)
{
    protected override ProcessTimeout ResolveTimeout(DoublyHandledFixtureTimeout command) =>
        new("FixtureExpiry", TimeSpan.FromMinutes(10));
}

/// <summary>Offender: named, but nothing handles it.</summary>
[InternalCommandName("Fixtures.UnhandledTimeout")]
internal sealed class UnhandledFixtureTimeout(int processId) : IProcessTimeoutCommand<int>
{
    public int ProcessId { get; } = processId;
}
