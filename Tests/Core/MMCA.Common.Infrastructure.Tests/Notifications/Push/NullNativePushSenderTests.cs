using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Infrastructure.Tests.Notifications.Push;

/// <summary>
/// The default native push sender (ADR-044) attempts nothing, so its outcome is empty and the
/// handler stores no per-platform counts (null is "not attempted").
/// </summary>
public sealed class NullNativePushSenderTests
{
    [Fact]
    public async Task SendToUsersAsync_ReturnsTheEmptyOutcome()
    {
        var outcome = await new NullNativePushSender().SendToUsersAsync([1, 2, 3], "title", "body", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Should().BeSameAs(NativePushOutcome.None);
        outcome.Android.Should().BeNull();
        outcome.Apple.Should().BeNull();
        outcome.HasFailures.Should().BeFalse();
    }
}
