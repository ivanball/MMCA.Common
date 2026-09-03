using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// Verifies the testable surface of <see cref="NotificationHubService"/>: constructor endpoint
/// validation, the idle lifecycle (not connected before start; stop/dispose are safe no-ops), and
/// the channel API's argument validation, subscription registry, and disconnected no-op paths.
/// The terminal-failure path of the retry loop IS covered, via the internal
/// <c>InitialRetryDelay</c> override plus an unroutable endpoint: it is the path that regressed
/// (H20), and a failed start used to leave the connection field populated so every later start
/// no-opped forever.
/// PARTIAL BY DESIGN: the successful <c>StartAsync</c>, the ReceiveNotification and
/// ReceiveChannelEvent wiring, and the join/re-join invocations build a real SignalR
/// <c>HubConnection</c> internally via <c>HubConnectionBuilder</c> with no injectable extension point, so
/// exercising them would need a live hub. Left uncovered rather than changing Source (covered end
/// to end by the consuming apps' E2E suites).
/// The reference-counted channel membership that decides WHETHER those invocations happen is
/// covered directly in <see cref="ChannelReferenceCounterTests"/>, since the decision is the part
/// that regressed and it is reachable without a connection.
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

    // ── Terminal connection failure must not poison later starts (H20) ──
    [Fact]
    public async Task StartAsync_AfterExhaustedRetries_RetriesOnNextCall()
    {
        var logger = new CapturingLogger();
        var tokenStorage = new Mock<ITokenStorageService>();

        // The access-token provider runs before the negotiate request, so throwing here fails every
        // attempt without any network wait.
        tokenStorage.Setup(t => t.GetAccessTokenAsync()).ThrowsAsync(new InvalidOperationException("no session"));

        await using var sut = new NotificationHubService(
            tokenStorage.Object,
            Options.Create(new ApiSettings { ApiEndpoint = "http://127.0.0.1:1" }),
            logger)
        {
            InitialRetryDelay = TimeSpan.Zero,
        };

        await sut.StartAsync();
        await sut.StartAsync();

        logger.FailureCount.Should().Be(
            2,
            "a start that never connected must leave no connection behind, or the second call no-ops forever");
        sut.IsConnected.Should().BeFalse();
    }

    // ── Concurrent starts must not each build a connection (L11) ──
    [Fact]
    public async Task StartAsync_CalledConcurrently_NeverRunsTwoStartsAtOnce()
    {
        // Two components calling JoinChannelAsync at the same time both used to see a null
        // connection field, both build one, and start both: the loser's socket leaked with
        // duplicate server registrations. Blazor Server has no single synchronization context,
        // so nothing else serialized them.
        var tokenStorage = new ConcurrencyTrackingTokenStorage();

        await using var sut = new NotificationHubService(
            tokenStorage,
            Options.Create(new ApiSettings { ApiEndpoint = "http://127.0.0.1:1" }),
            NullLogger<NotificationHubService>.Instance)
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        await Task.WhenAll(
            Task.Run(async () => await sut.StartAsync()),
            Task.Run(async () => await sut.StartAsync()));

        // The access-token provider runs inside the guarded body, so overlapping entries there mean
        // overlapping starts. Reverting the fix lets both callers in, and this reads 2.
        tokenStorage.MaxConcurrentCalls.Should().Be(1, "StartAsync must be serialized");
    }

    /// <summary>
    /// A token provider that records how many callers are inside it at once and then fails the
    /// attempt fast (the provider runs before the negotiate request, so no network wait). The short
    /// hold widens the overlap window: without it a revert could slip through unobserved. The
    /// flakiness is one-sided, so a false pass is only ever possible on the reverted code.
    /// </summary>
    private sealed class ConcurrencyTrackingTokenStorage : ITokenStorageService
    {
        private int _current;
        private int _max;

        public int MaxConcurrentCalls => Volatile.Read(ref _max);

        public async Task<string?> GetAccessTokenAsync()
        {
            var inside = Interlocked.Increment(ref _current);
            RecordMax(inside);
            try
            {
                await Task.Delay(50).ConfigureAwait(false);
                throw new InvalidOperationException("no session");
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);

        public Task SetTokensAsync(string accessToken, string refreshToken) => Task.CompletedTask;

        public Task ClearTokensAsync() => Task.CompletedTask;

        private void RecordMax(int inside)
        {
            int observed = Volatile.Read(ref _max);
            while (inside > observed)
            {
                int previous = Interlocked.CompareExchange(ref _max, inside, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    /// <summary>Counts the terminal "Failed to connect" warning; the retry loop's own messages are ignored.</summary>
    private sealed class CapturingLogger : ILogger<NotificationHubService>
    {
        private int _failureCount;

        public int FailureCount => Volatile.Read(ref _failureCount);

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

            if (formatter(state, exception).Contains("Failed to connect", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _failureCount);
            }
        }
    }
}

/// <summary>
/// Covers the reference-counted channel membership behind
/// <see cref="NotificationHubService.JoinChannelAsync"/> / <see cref="NotificationHubService.LeaveChannelAsync"/>.
/// The regression these lock down (H13): with the previous <c>HashSet</c> the first leaver removed the
/// only entry, so a page leaving a channel cut an invisible listener off from the same channel.
/// </summary>
public sealed class ChannelReferenceCounterTests
{
    [Fact]
    public void AddRef_FirstJoin_SignalsServerJoin_SecondDoesNot()
    {
        var sut = new ChannelReferenceCounter();

        sut.AddRef("event:1").Should().BeTrue("the server must be told to join on the 0 to 1 transition");
        sut.AddRef("event:1").Should().BeFalse("the connection is already in the group");
        sut.RefCountFor("event:1").Should().Be(2);
    }

    [Fact]
    public void Release_WithOutstandingRefs_DoesNotSignalServerLeave()
    {
        var sut = new ChannelReferenceCounter();
        sut.AddRef("event:1");
        sut.AddRef("event:1");

        bool shouldLeave = sut.Release("event:1");

        shouldLeave.Should().BeFalse("a second subscriber still holds the channel");
        sut.RefCountFor("event:1").Should().Be(1);
        sut.Snapshot().Should().Contain("event:1", "membership must survive one of two subscribers leaving");
    }

    [Fact]
    public void Release_OnLastRef_SignalsServerLeaveExactlyOnce()
    {
        var sut = new ChannelReferenceCounter();
        sut.AddRef("event:1");
        sut.AddRef("event:1");

        sut.Release("event:1").Should().BeFalse();
        sut.Release("event:1").Should().BeTrue("the last outstanding join releases the group membership");
        sut.Release("event:1").Should().BeFalse("an extra leave must not re-signal the server");

        sut.RefCountFor("event:1").Should().Be(0);
        sut.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Release_WithoutJoin_IsSafeNoOpAndNeverNegative()
    {
        var sut = new ChannelReferenceCounter();

        bool shouldLeave = sut.Release("never-joined");

        shouldLeave.Should().BeFalse();
        sut.RefCountFor("never-joined").Should().Be(0, "the count must never go negative");
        sut.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_ReturnsDistinctKeysWithOutstandingRefs()
    {
        var sut = new ChannelReferenceCounter();
        sut.AddRef("event:1");
        sut.AddRef("event:1");
        sut.AddRef("event:2");
        sut.AddRef("event:3");
        sut.Release("event:3");

        // Reconnect replay: one re-join per held channel, regardless of how many subscribers hold it.
        sut.Snapshot().Should().BeEquivalentTo("event:1", "event:2");
    }
}
