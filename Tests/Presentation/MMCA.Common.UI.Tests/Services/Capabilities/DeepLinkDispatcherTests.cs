using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Navigation;

namespace MMCA.Common.UI.Tests.Services.Capabilities;

/// <summary>
/// Covers <see cref="DeepLinkDispatcher"/>: live dispatch when a listener is attached, the
/// single-entry cold-start buffer (last-write-wins, consumed exactly once), argument
/// validation, and A-27: one route reaches exactly one listener (the newest still attached) and
/// the same route republished inside the duplicate window is one delivery.
/// </summary>
public sealed class DeepLinkDispatcherTests
{
    private const string SponsorRoute = "/engage/sponsors/5";
    private const string RoomRoute = "/engage/rooms/3";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 14, 0, 0, TimeSpan.Zero));

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

    /// <summary>
    /// The warm-boot deep link: a native callback publishes while the listener component is
    /// subscribing and draining. Reading the handler outside the lock allowed
    /// publish-sees-nothing, subscribe, drain-finds-nothing, buffer, which strands the route with
    /// no future consumer.
    /// <para>
    /// The lock placement is the guarantee; this is a regression net, not a proof. The window is a
    /// few instructions wide, so a reverted fix fails this probabilistically rather than always.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Publish_ConcurrentWithSubscribeAndDrain_NeverLosesTheRoute()
    {
        for (var i = 0; i < 2000; i++)
        {
            var sut = new DeepLinkDispatcher();
            var deliveries = 0;
            using var barrier = new Barrier(2);

            var publisher = Task.Run(() =>
            {
                barrier.SignalAndWait();
                sut.Publish("/conference/sessions/42");
            });

            var listener = Task.Run(() =>
            {
                barrier.SignalAndWait();
                sut.RouteRequested += (_, _) => Interlocked.Increment(ref deliveries);
                if (sut.TryConsumePending(out _))
                {
                    Interlocked.Increment(ref deliveries);
                }
            });

            await Task.WhenAll(publisher, listener);

            deliveries.Should().Be(
                1,
                "the listener must receive the route exactly once, through the event or through its own drain");
            sut.TryConsumePending(out _).Should().BeFalse(
                "a route left in the buffer after the listener has subscribed and drained has no future consumer");
        }
    }

    [Fact]
    public void Publish_WithTwoListenersAttached_DeliversOnlyToTheNewest()
    {
        var sut = new DeepLinkDispatcher();
        var older = new List<string>();
        var newer = new List<string>();
        sut.RouteRequested += (_, e) => older.Add(e.Route);
        sut.RouteRequested += (_, e) => newer.Add(e.Route);

        sut.Publish(SponsorRoute);

        newer.Should().Equal(SponsorRoute);
        older.Should().BeEmpty("a second listener must not turn one scan into a second visit post (A-27)");
    }

    [Fact]
    public void Publish_TheSameRouteTwiceInARow_IsOneDelivery()
    {
        var sut = new DeepLinkDispatcher();
        var delivered = new List<string>();
        sut.RouteRequested += (_, e) => delivered.Add(e.Route);

        sut.Publish(SponsorRoute);
        sut.Publish(SponsorRoute);

        delivered.Should().Equal(SponsorRoute);
    }

    [Fact]
    public void Publish_AfterTheNewestListenerDetaches_DeliversToTheOneStillAttached()
    {
        var sut = new DeepLinkDispatcher(_clock);
        var older = new List<string>();
        sut.RouteRequested += (_, e) => older.Add(e.Route);
        sut.RouteRequested += Newer;
        sut.RouteRequested -= Newer;

        sut.Publish(SponsorRoute);

        older.Should().Equal(SponsorRoute);

        static void Newer(object? sender, DeepLinkRouteEventArgs e) =>
            throw new InvalidOperationException("detached listener invoked");
    }

    [Fact]
    public void Publish_AfterAnOlderListenerResubscribes_DeliversToIt()
    {
        var sut = new DeepLinkDispatcher(_clock);
        var first = new List<string>();
        var second = new List<string>();
        sut.RouteRequested += First;
        sut.RouteRequested += (_, e) => second.Add(e.Route);
        sut.RouteRequested += First;

        sut.Publish(SponsorRoute);

        first.Should().Equal(SponsorRoute);
        second.Should().BeEmpty("subscribing again moves a listener to the end of the order");

        void First(object? sender, DeepLinkRouteEventArgs e) => first.Add(e.Route);
    }

    [Fact]
    public void Publish_TheSameRouteAgainInsideTheWindow_IsOneDelivery()
    {
        var sut = new DeepLinkDispatcher(_clock);
        var delivered = new List<string>();
        sut.RouteRequested += (_, e) => delivered.Add(e.Route);

        sut.Publish(SponsorRoute);
        _clock.Advance(DeepLinkDispatcher.DuplicateWindow - TimeSpan.FromMilliseconds(1));
        sut.Publish(SponsorRoute);

        delivered.Should().Equal(SponsorRoute);
    }

    [Fact]
    public void Publish_TheSameRouteWhileBuffered_IsNotDrainedTwice()
    {
        var sut = new DeepLinkDispatcher(_clock);

        sut.Publish(SponsorRoute);
        sut.TryConsumePending(out _).Should().BeTrue();

        // The launch intent republished on recreation, after the cold-start drain already took it.
        sut.Publish(SponsorRoute);

        sut.TryConsumePending(out _).Should().BeFalse();
    }

    [Fact]
    public void Publish_TheSameRouteAfterTheWindow_IsDeliveredAgain()
    {
        var sut = new DeepLinkDispatcher(_clock);
        var delivered = new List<string>();
        sut.RouteRequested += (_, e) => delivered.Add(e.Route);

        sut.Publish(SponsorRoute);
        _clock.Advance(DeepLinkDispatcher.DuplicateWindow);
        sut.Publish(SponsorRoute);

        delivered.Should().Equal(SponsorRoute, SponsorRoute);
    }

    [Fact]
    public void Publish_ADifferentRouteInsideTheWindow_IsDelivered()
    {
        var sut = new DeepLinkDispatcher(_clock);
        var delivered = new List<string>();
        sut.RouteRequested += (_, e) => delivered.Add(e.Route);

        sut.Publish(SponsorRoute);
        sut.Publish(RoomRoute);

        delivered.Should().Equal(SponsorRoute, RoomRoute);
    }

    [Fact]
    public void Constructor_RejectsANullClock()
    {
        var act = () => new DeepLinkDispatcher(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_ResolvesWithOrWithoutARegisteredClock(bool registerClock)
    {
        // The capability defaults register the type by implementation: the container must still pick
        // a constructor whether or not the host registers a TimeProvider.
        var services = new ServiceCollection();
        if (registerClock)
        {
            services.AddSingleton<TimeProvider>(_clock);
        }

        services.AddDeviceCapabilityDefaults();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDeepLinkDispatcher>().Should().BeOfType<DeepLinkDispatcher>();
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

    [Theory]
    [InlineData("/orders/7", null, null, "/orders/7")]
    [InlineData("/checkout/return", "order=7&payment=success", null, "/checkout/return?order=7&payment=success")]
    [InlineData("/orders/guest/7", null, "token=a%2Bb", "/orders/guest/7#token=a%2Bb")]
    [InlineData("/confirm-email", "userId=3", "code=x%3Dy", "/confirm-email?userId=3#code=x%3Dy")]
    [InlineData("/catalog/a%3Fb", "", "", "/catalog/a%3Fb")]
    public void TryComposeRoute_AppRelativeParts_KeepsQueryAndFragmentEncoded(
        string path,
        string? query,
        string? fragment,
        string expected)
    {
        DeepLinkDispatcher.TryComposeRoute(path, query, fragment, out var route).Should().BeTrue();

        route.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//attacker.example/p")]
    [InlineData("orders/7")]
    [InlineData("/\\attacker.example")]
    public void TryComposeRoute_PathThatIsNotAppRelative_ReturnsFalse(string? path)
    {
        DeepLinkDispatcher.TryComposeRoute(path, "q=1", "f", out var route).Should().BeFalse();

        route.Should().BeNull();
    }
}
