using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Application.Users;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// SEC-Common-37: a cacheable query addressed at one user must not be served to another from the
/// same cache entry. The decorator folds the target user into the key automatically for a query
/// that carries <see cref="IUserScopedRequest"/>, and <see cref="ISharedQueryCache"/> opts back out.
/// </summary>
public sealed class CallerScopedQueryCacheTests
{
    public sealed record MyOrdersQuery(UserIdentifierType UserId) : IQueryCacheable, IUserScopedRequest
    {
        public string CacheKey => "Sales:MyOrders:page=1";

        public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
    }

    public sealed record PublicCardQuery(UserIdentifierType UserId)
        : IQueryCacheable, IUserScopedRequest, ISharedQueryCache
    {
        public string CacheKey => "Sales:PublicCard";

        public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
    }

    public sealed record UnscopedQuery : IQueryCacheable
    {
        public string CacheKey => "Catalog:Products:page=1";

        public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
    }

    private static (CachingQueryDecorator<TQuery, Result<string>> Decorator, List<string> ReadKeys, List<string> WrittenKeys)
        Build<TQuery>(string handlerResult)
    {
        var readKeys = new List<string>();
        var writtenKeys = new List<string>();

        var inner = new Mock<IQueryHandler<TQuery, Result<string>>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(handlerResult));

        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.GetAsync<Result<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((key, _) => readKeys.Add(key))
            .ReturnsAsync((Result<string>?)null);
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Result<string>, TimeSpan?, CancellationToken>((key, _, _, _) => writtenKeys.Add(key))
            .Returns(Task.CompletedTask);

        var decorator = new CachingQueryDecorator<TQuery, Result<string>>(
            inner.Object,
            cache.Object,
            NullLogger<CachingQueryDecorator<TQuery, Result<string>>>.Instance);

        return (decorator, readKeys, writtenKeys);
    }

    [Fact]
    public async Task UserScopedQuery_KeysTheCacheEntryPerCaller()
    {
        var (decorator, readKeys, writtenKeys) = Build<MyOrdersQuery>("orders");

        await decorator.HandleAsync(new MyOrdersQuery(7), TestContext.Current.CancellationToken);
        await decorator.HandleAsync(new MyOrdersQuery(8), TestContext.Current.CancellationToken);

        // Two callers, two distinct keys: neither can be served the other's rows.
        writtenKeys.Should().HaveCount(2);
        writtenKeys.Distinct(StringComparer.Ordinal).Should().HaveCount(2);
        writtenKeys.Should().AllSatisfy(k => k.Should().StartWith("Sales:MyOrders:page=1"));
        writtenKeys[0].Should().EndWith(":u:7");
        writtenKeys[1].Should().EndWith(":u:8");

        // Reads use the same keys, or a hit would never be found.
        readKeys.Should().Contain(writtenKeys);
    }

    [Fact]
    public async Task UserScopedKey_KeepsTheAuthoredKeyAsItsPrefix()
    {
        var (decorator, _, writtenKeys) = Build<MyOrdersQuery>("orders");

        await decorator.HandleAsync(new MyOrdersQuery(7), TestContext.Current.CancellationToken);

        // The caller segment is appended, not prepended, so an ICacheInvalidating command that
        // evicts by the authored prefix still clears every caller's copy.
        writtenKeys.Single().Should().StartWith("Sales:MyOrders:page=1");
    }

    [Fact]
    public async Task SharedQueryCacheMarker_OptsOutOfCallerScoping()
    {
        var (decorator, _, writtenKeys) = Build<PublicCardQuery>("card");

        await decorator.HandleAsync(new PublicCardQuery(7), TestContext.Current.CancellationToken);
        await decorator.HandleAsync(new PublicCardQuery(8), TestContext.Current.CancellationToken);

        writtenKeys.Should().AllBe("Sales:PublicCard");
    }

    [Fact]
    public async Task QueryWithoutTheUserMarker_KeepsItsKeyByteIdentical()
    {
        var (decorator, _, writtenKeys) = Build<UnscopedQuery>("products");

        await decorator.HandleAsync(new UnscopedQuery(), TestContext.Current.CancellationToken);

        writtenKeys.Single().Should().Be("Catalog:Products:page=1");
    }
}
