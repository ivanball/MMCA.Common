using AwesomeAssertions;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Tests.Services.Capabilities;

/// <summary>
/// Covers <see cref="DeepLinkDispatcher"/>: live dispatch when a listener is attached, the
/// single-entry cold-start buffer (last-write-wins, consumed exactly once), and argument
/// validation.
/// </summary>
public sealed class DeepLinkDispatcherTests
{
    [Fact]
    public void Publish_WithoutListener_BuffersRouteForSingleConsumption()
    {
        var sut = new DeepLinkDispatcher();

        sut.Publish("/happening-now");

        sut.TryConsumePending(out var route).Should().BeTrue();
        route.Should().Be("/happening-now");
        sut.TryConsumePending(out var second).Should().BeFalse();
        second.Should().BeNull();
    }

    [Fact]
    public void Publish_WithoutListener_LastWriteWins()
    {
        var sut = new DeepLinkDispatcher();

        sut.Publish("/first");
        sut.Publish("/second");

        sut.TryConsumePending(out var route).Should().BeTrue();
        route.Should().Be("/second");
    }

    [Fact]
    public void Publish_WithListener_RaisesEventAndDoesNotBuffer()
    {
        var sut = new DeepLinkDispatcher();
        var received = new List<string>();
        sut.RouteRequested += (_, args) => received.Add(args.Route);

        sut.Publish("/conference/sessions/42");

        received.Should().ContainSingle().Which.Should().Be("/conference/sessions/42");
        sut.TryConsumePending(out _).Should().BeFalse();
    }

    [Fact]
    public void Publish_AfterListenerDetaches_BuffersAgain()
    {
        var sut = new DeepLinkDispatcher();
        static void Listener(object? sender, DeepLinkRouteEventArgs args)
        {
            // No-op: only attachment matters for this test.
        }

        sut.RouteRequested += Listener;
        sut.RouteRequested -= Listener;

        sut.Publish("/notifications");

        sut.TryConsumePending(out var route).Should().BeTrue();
        route.Should().Be("/notifications");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Publish_RejectsNullOrWhitespaceRoutes(string? route)
    {
        var sut = new DeepLinkDispatcher();

        var act = () => sut.Publish(route!);

        act.Should().Throw<ArgumentException>();
    }
}
