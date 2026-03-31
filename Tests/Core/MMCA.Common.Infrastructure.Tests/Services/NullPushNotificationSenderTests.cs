using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class NullPushNotificationSenderTests
{
    private readonly NullPushNotificationSender _sut = new();

    // ── SendToUserAsync ──
    [Fact]
    public async Task SendToUserAsync_CompletesWithoutException() =>
        await FluentActions.Invoking(() =>
                _sut.SendToUserAsync(1, "title", "body"))
            .Should().NotThrowAsync();

    // ── SendToUsersAsync ──
    [Fact]
    public async Task SendToUsersAsync_CompletesWithoutException() =>
        await FluentActions.Invoking(() =>
                _sut.SendToUsersAsync([1, 2, 3], "title", "body"))
            .Should().NotThrowAsync();

    // ── BroadcastAsync ──
    [Fact]
    public async Task BroadcastAsync_CompletesWithoutException() =>
        await FluentActions.Invoking(() =>
                _sut.BroadcastAsync("title", "body"))
            .Should().NotThrowAsync();

    // ── With metadata ──
    [Fact]
    public async Task SendToUserAsync_WithMetadata_CompletesWithoutException() =>
        await FluentActions.Invoking(() =>
                _sut.SendToUserAsync(1, "title", "body", new Dictionary<string, string> { ["key"] = "value" }))
            .Should().NotThrowAsync();
}
