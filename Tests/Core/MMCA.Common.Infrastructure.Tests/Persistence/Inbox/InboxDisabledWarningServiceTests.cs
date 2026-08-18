using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Persistence.Inbox;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Inbox;

/// <summary>
/// Unit tests for <see cref="InboxDisabledWarningService"/>: the one startup line that keeps a
/// disabled dedup store from looking exactly like an enabled one.
/// </summary>
public sealed class InboxDisabledWarningServiceTests
{
    /// <summary>
    /// Hand-rolled instead of a <c>Mock&lt;ILogger&lt;T&gt;&gt;</c>: the service under test is
    /// internal, and Castle DynamicProxy cannot build a proxy for a closed generic over an
    /// internal type when the generic definition lives in the strong-named
    /// Microsoft.Extensions.Logging.Abstractions assembly.
    /// </summary>
    private sealed class RecordingLogger : ILogger<InboxDisabledWarningService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task StartAsync_LogsExactlyOneWarningNamingTheSettingThatTurnsTheInboxOn()
    {
        var logger = new RecordingLogger();
        var sut = new InboxDisabledWarningService(logger);

        await sut.StartAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle("the warning is a startup posture statement, not a per-message log");
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("MessageBus:EnableInbox=true", "the log must carry its own remedy");
        logger.Entries[0].Message.Should().Contain("at-least-once");
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutLogging()
    {
        var logger = new RecordingLogger();
        var sut = new InboxDisabledWarningService(logger);

        await sut.StopAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }
}
