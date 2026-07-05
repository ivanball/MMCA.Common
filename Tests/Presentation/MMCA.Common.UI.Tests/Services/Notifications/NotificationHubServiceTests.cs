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
/// validation and the idle lifecycle (not connected before start; stop/dispose are safe no-ops).
/// PARTIAL BY DESIGN: <c>StartAsync</c>, the retry/backoff loop, and the ReceiveNotification
/// callback wiring build a real SignalR <c>HubConnection</c> internally via
/// <c>HubConnectionBuilder</c> with no injectable seam, so exercising them would attempt real
/// network connections with multi-second backoff. Left uncovered rather than changing Source.
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
}
