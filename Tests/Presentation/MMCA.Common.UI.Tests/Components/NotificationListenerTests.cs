using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components.Notifications;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Covers <see cref="NotificationListener"/>, the invisible component that turns hub notifications
/// into badge state and a toast. The regression these lock down (M54): the callback dispatched a
/// render without guarding the disposed-component race, so an event arriving after the page had
/// navigated away threw back into the hub service's own receive handler.
/// </summary>
public sealed class NotificationListenerTests : BunitTestBase
{
    private readonly NotificationState _state = new();
    private readonly Mock<IToastService> _toast = new();

    public NotificationListenerTests()
    {
        Services.AddSingleton(_state);

        // Registered after the base class's default facade, so this wins: it is the only way to make
        // the dispatched body throw the way a torn-down circuit does.
        Services.AddSingleton(_toast.Object);
        Services.AddSingleton(_ =>
        {
            var tokenStorage = new Mock<ITokenStorageService>();

            // The access-token provider runs before the negotiate request, so throwing here makes the
            // background connect attempt fail immediately instead of waiting on a real socket.
            tokenStorage
                .Setup(t => t.GetAccessTokenAsync())
                .ThrowsAsync(new InvalidOperationException("no session"));

            return new NotificationHubService(
                tokenStorage.Object,
                Options.Create(new ApiSettings { ApiEndpoint = "http://127.0.0.1:1" }),
                NullLogger<NotificationHubService>.Instance)
            {
                InitialRetryDelay = TimeSpan.Zero,
            };
        });
    }

    [Fact]
    public async Task NotificationWhileRendered_IncrementsUnreadAndRequestsRefresh()
    {
        bool refreshRequested = false;
        _state.OnRefreshRequested += (_, _) => refreshRequested = true;
        Func<string, string, Task> callback = await RenderAndCaptureCallbackAsync();

        await callback("Title", "Body");

        _state.UnreadCount.Should().Be(1, "the badge updates optimistically, before any API round-trip");
        refreshRequested.Should().BeTrue("NotificationBell still fetches the authoritative count");
    }

    // bUnit's dispatcher keeps working after the components are disposed, so the disposed-renderer
    // race cannot be staged by disposal alone: the exception it raises is injected at the dispatched
    // body instead. Both types the guard names are exercised.
    [Fact]
    public Task NotificationDispatchThrowingObjectDisposed_DoesNotThrowBackAtTheHub() =>
        AssertDispatchExceptionIsSwallowedAsync(new ObjectDisposedException("Renderer"));

    [Fact]
    public Task NotificationDispatchThrowingInvalidOperation_DoesNotThrowBackAtTheHub() =>
        AssertDispatchExceptionIsSwallowedAsync(new InvalidOperationException("The renderer has been disposed."));

    private async Task AssertDispatchExceptionIsSwallowedAsync(Exception raised)
    {
        _toast
            .Setup(t => t.ShowPersistent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ToastSeverity>()))
            .Throws(raised);
        Func<string, string, Task> callback = await RenderAndCaptureCallbackAsync();

        await DisposeComponentsAsync();

        Func<Task> raise = () => callback("Title", "Body");

        await raise.Should().NotThrowAsync(
            "a render dispatched onto a disposed component must not surface inside the hub's receive handler");
    }

    private async Task<Func<string, string, Task>> RenderAndCaptureCallbackAsync()
    {
        IRenderedComponent<NotificationListener> cut = RenderAs<NotificationListener>(
            TestPrincipal.AuthenticatedUser(), _ => { });

        NotificationHubService hubService = Services.GetRequiredService<NotificationHubService>();

        // The callback is assigned in the component's first-render hook, before the hub connect.
        await cut.WaitForAssertionAsync(() => hubService.NotificationCallback.Should().NotBeNull());

        return hubService.NotificationCallback!;
    }
}
