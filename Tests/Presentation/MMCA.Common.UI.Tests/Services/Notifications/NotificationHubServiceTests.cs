#pragma warning disable CA2000 // Dispose objects before losing scope - the idempotent-dispose test disposes explicitly

using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies the testable surface of <see cref="NotificationHubService"/>: constructor endpoint
/// validation, the idle lifecycle (not connected before start; stop/dispose are safe no-ops), and
/// the channel API's argument validation, subscription registry, and disconnected no-op paths.
/// PARTIAL BY DESIGN: <c>StartAsync</c>, the retry/backoff loop, the ReceiveNotification and
/// ReceiveChannelEvent wiring, and the join/re-join invocations build a real SignalR
/// <c>HubConnection</c> internally via <c>HubConnectionBuilder</c> with no injectable seam, so
/// exercising them would attempt real network connections with multi-second backoff. Left
/// uncovered rather than changing Source (covered end to end by the consuming apps' E2E suites).
/// </summary>
public sealed class NotificationHubServiceTests
{
    private static NotificationHubService CreateSut(IOptions<ApiSettings>? apiSettings = null) =>
        new(
            new Mock<ITokenStorageService>().Object,
            apiSettings ?? Options.Create(new ApiSettings { ApiEndpoint = "https://api.example.com/" }),
            NullLogger<NotificationHubService>.Instance);

    [Fact]
    public void Ctor_WithNullApiSettings_ThrowsArgumentNull()
    {
        var act = () => new NotificationHubService(
            new Mock<ITokenStorageService>().Object,
            null!,
            NullLogger<NotificationHubService>.Instance);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_WithMissingApiEndpoint_ThrowsArgumentNull()
    {
        var act = () => CreateSut(Options.Create(new ApiSettings()));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task IsConnected_BeforeStart_IsFalse()
    {
        await using var sut = CreateSut();

        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        await using var sut = CreateSut();

        var act = () => sut.StopAsync();

        await act.Should().NotThrowAsync();
        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_DoesNotThrowAndIsIdempotent()
    {
        var sut = CreateSut();

        var act = async () =>
        {
            await sut.DisposeAsync();
            await sut.DisposeAsync();
        };

        await act.Should().NotThrowAsync();
    }

    // ── Channel API: argument validation ──
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task JoinChannelAsync_WithBlankChannel_ThrowsArgumentException(string channelKey)
    {
        await using var sut = CreateSut();

        var act = () => sut.JoinChannelAsync(channelKey);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task LeaveChannelAsync_WithBlankChannel_ThrowsArgumentException(string channelKey)
    {
        await using var sut = CreateSut();

        var act = () => sut.LeaveChannelAsync(channelKey);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task OnChannelEvent_WithBlankChannel_ThrowsArgumentException()
    {
        await using var sut = CreateSut();

        var act = () => sut.OnChannelEvent(" ", (_, _) => Task.CompletedTask);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task OnChannelEvent_WithNullHandler_ThrowsArgumentNull()
    {
        await using var sut = CreateSut();

        var act = () => sut.OnChannelEvent("event:1", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Channel API: subscription lifecycle ──
    [Fact]
    public async Task OnChannelEvent_ReturnsSubscription_WhoseDisposeIsIdempotent()
    {
        await using var sut = CreateSut();

        IDisposable subscription = sut.OnChannelEvent("event:1", (_, _) => Task.CompletedTask);

        var act = () =>
        {
            subscription.Dispose();
            subscription.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task OnChannelEvent_AllowsMultipleSubscribersOnSameChannel()
    {
        await using var sut = CreateSut();

        IDisposable first = sut.OnChannelEvent("event:1", (_, _) => Task.CompletedTask);
        IDisposable second = sut.OnChannelEvent("event:1", (_, _) => Task.CompletedTask);

        var act = () =>
        {
            first.Dispose();
            second.Dispose();
        };

        act.Should().NotThrow();
    }

    // ── Channel API: disconnected no-op ──
    [Fact]
    public async Task LeaveChannelAsync_WithoutConnection_DoesNotThrow()
    {
        await using var sut = CreateSut();

        var act = () => sut.LeaveChannelAsync("event:1");

        await act.Should().NotThrowAsync();
        sut.IsConnected.Should().BeFalse();
    }
}
