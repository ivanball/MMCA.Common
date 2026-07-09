using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class NullLiveChannelPublisherTests
{
    private readonly NullLiveChannelPublisher _sut = new();

    // ── PublishAsync ──
    [Fact]
    public async Task PublishAsync_CompletesWithoutException() =>
        await FluentActions.Invoking(() =>
                _sut.PublishAsync("event:1", "poll.results-changed", "{}"))
            .Should().NotThrowAsync();

    // ── With cancellation token ──
    [Fact]
    public async Task PublishAsync_WithCancellationToken_CompletesWithoutException()
    {
        using var cts = new CancellationTokenSource();

        await FluentActions.Invoking(() =>
                _sut.PublishAsync("session:42", "question.approved", "{}", cts.Token))
            .Should().NotThrowAsync();
    }
}
