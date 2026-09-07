using AwesomeAssertions;
using MMCA.Common.UI.Services.Capabilities.Navigation;

namespace MMCA.Common.UI.Tests.Services.Capabilities;

/// <summary>
/// SEC-Common-88 / SEC-ADC-65: a deep-link route crosses a process boundary from an untrusted
/// caller on a native head, and an exported activity is reachable by an EXPLICIT intent that
/// bypasses the manifest's scheme and host filters. The dispatcher therefore refuses anything that
/// is not an app-origin-relative path, which is where the protocol-relative escape
/// (<c>//attacker.example/p</c>) is stopped.
/// </summary>
public sealed class DeepLinkRouteShapeTests
{
    [Theory]
    [InlineData("//attacker.example/p")]
    [InlineData("///attacker.example")]
    [InlineData("/\attacker.example")]
    [InlineData("https://attacker.example/p")]
    [InlineData("http://attacker.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("conference/sessions/42")]
    [InlineData("/")]
    [InlineData("/ok\r\nSet-Cookie: x=1")]
    public void Publish_RejectsARouteThatIsNotAppRelative(string route)
    {
        var sut = new DeepLinkDispatcher();

        Action publish = () => sut.Publish(route);

        publish.Should().Throw<ArgumentException>();
        sut.TryConsumePending(out _).Should().BeFalse(
            because: "a refused route must not be buffered for the router to consume later either");
    }

    [Theory]
    [InlineData("/happening-now")]
    [InlineData("/conference/sessions/42")]
    [InlineData("/search?q=blazor&page=2")]
    [InlineData("/sessions/42#agenda")]
    public void Publish_AcceptsAnAppRelativeRoute(string route)
    {
        var sut = new DeepLinkDispatcher();

        sut.Publish(route);

        sut.TryConsumePending(out var pending).Should().BeTrue();
        pending.Should().Be(route);
    }

    [Fact]
    public void Publish_RefusesBeforeRaisingTheEvent()
    {
        var sut = new DeepLinkDispatcher();
        var raised = 0;
        sut.RouteRequested += (_, _) => raised++;

        Action publish = () => sut.Publish("//attacker.example/p");

        publish.Should().Throw<ArgumentException>();
        raised.Should().Be(0, "an attached listener navigates on the callback, so the guard has to run first");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("/ok", true)]
    public void IsAppRelativeRoute_IsHostileInputTolerant(string? route, bool expected)
        => DeepLinkDispatcher.IsAppRelativeRoute(route).Should().Be(expected);
}
