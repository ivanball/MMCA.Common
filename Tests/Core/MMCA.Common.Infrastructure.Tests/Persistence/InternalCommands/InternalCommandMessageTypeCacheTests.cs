using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// The internal-command twin of the outbox type cache (M183): only a successful resolution is
/// cached, consistent with OutboxMessage. Defensive here: the processor dead-letters a row with an
/// unresolvable type on its first attempt (ADR-114), so the pin is that no null is cached.
/// </summary>
public sealed class InternalCommandMessageTypeCacheTests
{
    [Fact]
    public void DeserializeCommand_DoesNotCacheAnUnresolvableName()
    {
        const string neverDeclared = "MMCA.Tests.Never.Declared.Command.v1";

        new InternalCommandMessage { CommandType = neverDeclared, Payload = "{}" }.DeserializeCommand().Should().BeNull();

        InternalCommandMessage.IsCommandTypeCached(neverDeclared).Should().BeFalse(
            "a cached null would turn every later attempt into a guaranteed failure for a late-loading assembly");
    }
}
