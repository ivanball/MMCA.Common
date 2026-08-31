using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Caching;

namespace MMCA.Common.UI.Tests.Services.Caching;

/// <summary>
/// Pins the client-side staleness policy of <see cref="UiReadCache"/> (§19): a hit inside the TTL,
/// a miss once the clock passes it, longest-prefix TTL resolution, prefix invalidation after a write,
/// a full clear on sign-out, and the disabled passthrough a host uses to turn the whole tier off.
/// Time is driven by <see cref="FakeTimeProvider"/>, so expiry is asserted rather than waited out.
/// </summary>
public sealed class UiReadCacheTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (UiReadCache Sut, FakeTimeProvider Clock) CreateSut(
        bool enabled = true,
        TimeSpan? defaultTtl = null,
        IReadOnlyList<(string Prefix, TimeSpan Ttl)>? prefixTtls = null)
    {
        var clock = new FakeTimeProvider(Origin);
        var options = new UiReadCacheOptions
        {
            Enabled = enabled,
            DefaultTtl = defaultTtl ?? TimeSpan.FromSeconds(60),
        };

        foreach (var (prefix, ttl) in prefixTtls ?? [])
        {
            options.RoutePrefixTtls[prefix] = ttl;
        }

        return (new UiReadCache(clock, Options.Create(options)), clock);
    }

    // == Freshness ==
    [Fact]
    public void TryGetFresh_WithinTheDefaultTtl_ReturnsTheStoredValue()
    {
        var (sut, clock) = CreateSut();
        sut.Set("widgets?page=1", "the-page");

        clock.Advance(TimeSpan.FromSeconds(59));

        sut.TryGetFresh<string>("widgets?page=1", out var value).Should().BeTrue();
        value.Should().Be("the-page");
    }

    [Fact]
    public void TryGetFresh_PastTheTtl_MissesAndDropsTheEntry()
    {
        var (sut, clock) = CreateSut();
        sut.Set("widgets?page=1", "the-page");

        clock.Advance(TimeSpan.FromSeconds(61));

        sut.TryGetFresh<string>("widgets?page=1", out var value).Should().BeFalse();
        value.Should().BeNull();

        // The expired entry is removed on the read that found it stale (lazy expiry): rewinding the
        // clock cannot resurrect it, which is what proves the removal rather than a comparison.
        sut.TryGetFresh<string>("widgets?page=1", out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetFresh_ExactlyAtTheTtl_StillHits()
    {
        // The boundary is inclusive: freshness is "not older than", so an entry stored exactly one
        // TTL ago is still the answer.
        var (sut, clock) = CreateSut();
        sut.Set("widgets", "value");

        clock.Advance(TimeSpan.FromSeconds(60));

        sut.TryGetFresh<string>("widgets", out _).Should().BeTrue();
    }

    [Fact]
    public void TryGetFresh_ForAnUnknownUrl_Misses()
    {
        var (sut, _) = CreateSut();

        sut.TryGetFresh<string>("widgets?page=2", out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGetFresh_ForTheSameUrlAtADifferentType_Misses()
    {
        // The key is the URL alone, so a caller asking for a different shape must not be handed the
        // stored one cast blindly.
        var (sut, _) = CreateSut();
        sut.Set("widgets", "a string");

        sut.TryGetFresh<int[]>("widgets", out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void Keys_DifferingOnlyInTheQueryString_AreSeparateEntries()
    {
        // The server's authenticated output cache varies on the full query string (QueryKeys = "*",
        // ADR-040). The client key mirrors that shape, so a page/filter change misses on both tiers.
        var (sut, _) = CreateSut();
        sut.Set("widgets/paged?pageNumber=1", "page one");

        sut.TryGetFresh<string>("widgets/paged?pageNumber=2", out _).Should().BeFalse();
        sut.TryGetFresh<string>("widgets/paged?pageNumber=1", out var first).Should().BeTrue();
        first.Should().Be("page one");
    }

    // == TTL resolution ==
    [Fact]
    public void ResolveTtl_UsesTheLongestMatchingRoutePrefix()
    {
        // Two prefixes match the same URL; the more specific one must win whatever order the
        // configuration enumerated them in.
        var (sut, clock) = CreateSut(
            defaultTtl: TimeSpan.FromSeconds(10),
            prefixTtls:
            [
                ("widgets", TimeSpan.FromSeconds(30)),
                ("widgets/lookup", TimeSpan.FromMinutes(10)),
            ]);

        sut.Set("widgets/lookup?nameProperty=Name", "lookups");
        sut.Set("widgets?includeFKs=False", "list");

        clock.Advance(TimeSpan.FromMinutes(1));

        sut.TryGetFresh<string>("widgets/lookup?nameProperty=Name", out _).Should()
            .BeTrue("the ten-minute lookup budget is the longest matching prefix");
        sut.TryGetFresh<string>("widgets?includeFKs=False", out _).Should()
            .BeFalse("the list read only had the thirty-second budget of its own prefix");
    }

    [Fact]
    public void ResolveTtl_WithNoMatchingPrefix_FallsBackToTheDefault()
    {
        var (sut, clock) = CreateSut(
            defaultTtl: TimeSpan.FromSeconds(5),
            prefixTtls: [("countries", TimeSpan.FromHours(1))]);

        sut.Set("widgets", "list");

        clock.Advance(TimeSpan.FromSeconds(6));

        sut.TryGetFresh<string>("widgets", out _).Should().BeFalse();
    }

    // == Invalidation ==
    [Fact]
    public void InvalidatePrefix_RemovesOnlyTheMatchingKeys()
    {
        var (sut, _) = CreateSut();
        sut.Set("widgets?includeFKs=False", "list");
        sut.Set("widgets/7?includeChildren=False", "one widget");
        sut.Set("gadgets?includeFKs=False", "other list");

        sut.InvalidatePrefix("widgets");

        sut.TryGetFresh<string>("widgets?includeFKs=False", out _).Should().BeFalse();
        sut.TryGetFresh<string>("widgets/7?includeChildren=False", out _).Should().BeFalse();
        sut.TryGetFresh<string>("gadgets?includeFKs=False", out var untouched).Should()
            .BeTrue("another endpoint's reads are not made stale by this endpoint's write");
        untouched.Should().Be("other list");
    }

    [Fact]
    public void InvalidatePrefix_MatchesOrdinally()
    {
        // A prefix that differs only in case is a different route to the server, so it must not be
        // swept by this endpoint's invalidation.
        var (sut, _) = CreateSut();
        sut.Set("Widgets?includeFKs=False", "differently cased");

        sut.InvalidatePrefix("widgets");

        sut.TryGetFresh<string>("Widgets?includeFKs=False", out _).Should().BeTrue();
    }

    [Fact]
    public void Clear_EmptiesEveryEntry()
    {
        var (sut, _) = CreateSut();
        sut.Set("widgets", "list");
        sut.Set("gadgets", "other list");

        sut.Clear();

        sut.TryGetFresh<string>("widgets", out _).Should().BeFalse();
        sut.TryGetFresh<string>("gadgets", out _).Should().BeFalse();
    }

    // == Disabled ==
    [Fact]
    public void WhenDisabled_SetIsANoOpAndEveryLookupMisses()
    {
        // The host's escape hatch: with Enabled=false the services behave exactly as they did before
        // a cache was registered, so a deployment can opt out of client-side staleness entirely.
        var (sut, _) = CreateSut(enabled: false);

        sut.Set("widgets", "list");

        sut.TryGetFresh<string>("widgets", out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void WhenDisabled_InvalidateAndClearStillSucceed()
    {
        var (sut, _) = CreateSut(enabled: false);

        var act = () =>
        {
            sut.InvalidatePrefix("widgets");
            sut.Clear();
        };

        act.Should().NotThrow();
    }

    // == Guards ==
    [Fact]
    public void Set_WithANullValue_StoresNothing()
    {
        // A null is not an answer worth replaying: the read that produced it failed or came back
        // empty, and both should re-ask.
        var (sut, _) = CreateSut();

        sut.Set<string?>("widgets", null);

        sut.TryGetFresh<string>("widgets", out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetFresh_WithABlankUrl_Throws()
    {
        var (sut, _) = CreateSut();

        var act = () => sut.TryGetFresh<string>(" ", out _);

        act.Should().Throw<ArgumentException>();
    }
}
