using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Markers;

namespace MMCA.Common.Application.Tests.UseCases.Markers;

/// <summary>
/// Pins the entity-keyed cache-invalidation marker: a command that implements
/// <c>ICacheInvalidating&lt;TEntity&gt;</c> and declares nothing gets the framework's
/// <c>"{typeof(TEntity).FullName}:"</c> prefix through the non-generic <c>ICacheInvalidating</c>
/// the decorator reads, and a command that declares its own <c>CachePrefix</c> keeps it.
/// Expected values are spelled out from <see cref="System.Type.FullName"/> rather than read back
/// from the helper, so the tests pin the value and not the implementation.
/// </summary>
public sealed class CacheInvalidatingTests
{
    private static readonly string OrderPrefix = $"{typeof(CacheMarkerTestOrder).FullName}:";

    // ── EntityCachePrefix ──
    [Fact]
    public void EntityCachePrefix_Generic_IsTheEntityFullNameFollowedByAColon() =>
        EntityCachePrefix.For<CacheMarkerTestOrder>().Should().Be(OrderPrefix);

    [Fact]
    public void EntityCachePrefix_ByType_IsTheEntityFullNameFollowedByAColon()
    {
        Type entityType = typeof(CacheMarkerTestOrder);

        EntityCachePrefix.For(entityType).Should().Be(OrderPrefix);
    }

    [Fact]
    public void EntityCachePrefix_EndsAtTheColonBoundaryTheCacheRequires() =>
        EntityCachePrefix.For<CacheMarkerTestOrder>().Should().EndWith(
            ":",
            "ICacheService.RemoveByPrefixAsync rejects a prefix that does not end at a ':' boundary");

    [Fact]
    public void EntityCachePrefix_DiffersPerEntity() =>
        EntityCachePrefix.For<CacheMarkerTestLine>().Should()
            .Be($"{typeof(CacheMarkerTestLine).FullName}:")
            .And.NotBe(OrderPrefix);

    [Fact]
    public void EntityCachePrefix_GenericAndByTypeOverloadsAgree()
    {
        Type entityType = typeof(CacheMarkerTestLine);

        EntityCachePrefix.For(entityType).Should().Be(EntityCachePrefix.For<CacheMarkerTestLine>());
    }

    // ── ICacheInvalidating<TEntity> default member ──
    [Fact]
    public void GenericMarker_WithNoDeclaredPrefix_ExposesTheEntityPrefixThroughTheNonGenericMarker() =>
        PrefixSeenByTheDecorator(new PlaceCacheMarkerTestOrderCommand(42)).Should().Be(OrderPrefix);

    [Fact]
    public void GenericMarker_WithNoDeclaredPrefix_EndsWithAColon() =>
        PrefixSeenByTheDecorator(new PlaceCacheMarkerTestOrderCommand(42)).Should().EndWith(":");

    [Fact]
    public void GenericMarker_PrefixFollowsTheTypeArgument() =>
        PrefixSeenByTheDecorator(new AddCacheMarkerTestLineCommand(7)).Should().Be($"{typeof(CacheMarkerTestLine).FullName}:");

    [Fact]
    public void GenericMarker_IsANonGenericMarker() =>
        typeof(ICacheInvalidating).IsAssignableFrom(typeof(ICacheInvalidating<CacheMarkerTestOrder>))
            .Should().BeTrue("the caching command decorator only ever reads the non-generic ICacheInvalidating");

    [Fact]
    public void GenericMarker_WithItsOwnPublicPrefix_TheClassMemberWins() =>
        PrefixSeenByTheDecorator(new CustomPrefixCacheMarkerTestOrderCommand(42)).Should().Be(
            "Orders:Custom:",
            "a command's own CachePrefix implements the interface member and takes precedence over the default");

    /// <summary>Reads the prefix the way CachingCommandDecorator does: through the non-generic marker.</summary>
    private static string PrefixSeenByTheDecorator(ICacheInvalidating command) => command.CachePrefix;
}

// ── Test types ──
public sealed class CacheMarkerTestOrder;

public sealed class CacheMarkerTestLine;

/// <summary>Declares no CachePrefix: the generic marker supplies it.</summary>
public sealed record PlaceCacheMarkerTestOrderCommand(int OrderId) : ICacheInvalidating<CacheMarkerTestOrder>;

/// <summary>Declares no CachePrefix, keyed to a different entity.</summary>
public sealed record AddCacheMarkerTestLineCommand(int LineId) : ICacheInvalidating<CacheMarkerTestLine>;

/// <summary>Implements the generic marker AND declares its own prefix.</summary>
public sealed record CustomPrefixCacheMarkerTestOrderCommand(int OrderId) : ICacheInvalidating<CacheMarkerTestOrder>
{
    public string CachePrefix => "Orders:Custom:";
}
