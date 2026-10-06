using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// The internal-command twin of the outbox type cache (M183): only a successful resolution is
/// cached, so an unresolvable stored name is re-scanned on its next attempt and a late-loading
/// assembly can still resolve it before the row dead-letters.
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
