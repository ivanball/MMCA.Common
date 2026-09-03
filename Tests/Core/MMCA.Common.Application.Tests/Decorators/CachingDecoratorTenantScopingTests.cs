using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Coverage for tenant isolation in the caching decorators. <c>ICacheService</c> is a singleton and
/// cannot see the scoped tenant, so the key transformation is the isolation: without it two tenants
/// computing the same query key would read each other's rows out of one cache entry.
/// </summary>
public sealed class CachingDecoratorTenantScopingTests
{
    private const string BareKey = "test-cache-key";
    private const string AcmeKey = "t:acme:test-cache-key";
    private const string BarePrefix = "test-prefix";
    private const string AcmePrefix = "t:acme:test-prefix";

    // ── Query decorator ──
    [Fact]
    public async Task Query_WithAResolvedTenant_ReadsAndWritesTheTenantScopedKey()
    {
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);

        var sut = QueryDecorator(cacheService, Resolved("acme"), "fresh");

        await sut.HandleAsync(new CacheableTestQuery());

        cacheService.Verify(x => x.GetAsync<Result<string>>(AcmeKey, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        cacheService.Verify(
            x => x.SetAsync(AcmeKey, It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        cacheService.Verify(x => x.GetAsync<Result<string>>(BareKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Query_WithNoTenant_KeepsTheBareKey()
    {
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);

        var sut = QueryDecorator(cacheService, Unresolved(), "fresh");

        await sut.HandleAsync(new CacheableTestQuery());

        cacheService.Verify(
            x => x.SetAsync(BareKey, It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a single-tenant host must keep exactly the keyspace it had before tenancy shipped");
    }

    [Fact]
    public async Task Query_WithoutATenantContextAtAll_KeepsTheBareKey()
    {
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);

        var sut = QueryDecorator(cacheService, tenantContext: null, "fresh");

        await sut.HandleAsync(new CacheableTestQuery());

        cacheService.Verify(
            x => x.SetAsync(BareKey, It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Query_OneTenantsEntry_IsNotServedToAnother()
    {
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>(AcmeKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("acme-data"));
        cacheService.Setup(x => x.GetAsync<Result<string>>("t:globex:test-cache-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);

        Result<string> acme = await QueryDecorator(cacheService, Resolved("acme"), "unused")
            .HandleAsync(new CacheableTestQuery());
        Result<string> globex = await QueryDecorator(cacheService, Resolved("globex"), "globex-data")
            .HandleAsync(new CacheableTestQuery());

        acme.Value.Should().Be("acme-data");
        globex.Value.Should().Be("globex-data", "the second tenant missed and ran its own handler");
    }

    // ── Command decorator ──
    [Fact]
    public async Task Command_WithAResolvedTenant_EvictsTheTenantScopedPrefix()
    {
        var cacheService = new Mock<ICacheService>();
        var sut = CommandDecorator(cacheService, Resolved("acme"));

        await sut.HandleAsync(new CacheInvalidatingTestCommand());

        // AtLeastOnce: the decorator also schedules a delayed re-eviction of the same prefix.
        cacheService.Verify(x => x.RemoveByPrefixAsync(AcmePrefix, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        cacheService.Verify(x => x.RemoveByPrefixAsync(BarePrefix, It.IsAny<CancellationToken>()), Times.Never,
            "evicting the bare prefix would take out every tenant's entries at once");
    }

    [Fact]
    public async Task Command_WithNoTenant_EvictsTheBarePrefix()
    {
        var cacheService = new Mock<ICacheService>();
        var sut = CommandDecorator(cacheService, Unresolved());

        await sut.HandleAsync(new CacheInvalidatingTestCommand());

        cacheService.Verify(x => x.RemoveByPrefixAsync(BarePrefix, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        cacheService.Verify(
            x => x.RemoveByPrefixAsync(It.Is<string>(p => p.StartsWith("t:", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Command_AndQuery_TransformTheKeyIdentically()
    {
        // Symmetry is the whole point: an entry written under the query's scoped key must be removed
        // by the command's scoped prefix, or invalidation silently stops working under tenancy.
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);

        string? writtenKey = null;
        cacheService.Setup(x => x.SetAsync(
                It.IsAny<string>(), It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Result<string>, TimeSpan?, CancellationToken>((key, _, _, _) => writtenKey = key)
            .Returns(Task.CompletedTask);

        string? evictedPrefix = null;
        cacheService.Setup(x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((prefix, _) => evictedPrefix = prefix)
            .Returns(Task.CompletedTask);

        await QueryDecorator(cacheService, Resolved("acme"), "fresh").HandleAsync(new CacheableTestQuery());
        await CommandDecorator(cacheService, Resolved("acme")).HandleAsync(new CacheInvalidatingTestCommand());

        writtenKey.Should().NotBeNull();
        evictedPrefix.Should().NotBeNull();
        writtenKey.Should().StartWith(evictedPrefix![..^"prefix".Length]);
        writtenKey.Should().StartWith("t:acme:");
    }

    // ── Scaffolding ──
    private static ITenantContext Resolved(string tenantId)
    {
        var context = new Mock<ITenantContext>();
        context.SetupGet(c => c.TenantId).Returns(tenantId);
        context.SetupGet(c => c.IsResolved).Returns(true);
        return context.Object;
    }

    private static ITenantContext Unresolved()
    {
        var context = new Mock<ITenantContext>();
        context.SetupGet(c => c.TenantId).Returns((string?)null);
        context.SetupGet(c => c.IsResolved).Returns(false);
        return context.Object;
    }

    private static CachingQueryDecorator<CacheableTestQuery, Result<string>> QueryDecorator(
        Mock<ICacheService> cacheService,
        ITenantContext? tenantContext,
        string handlerResult)
    {
        var inner = new Mock<IQueryHandler<CacheableTestQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(handlerResult));

        return new CachingQueryDecorator<CacheableTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheableTestQuery, Result<string>>>.Instance,
            tenantContext);
    }

    private static CachingCommandDecorator<CacheInvalidatingTestCommand, Result> CommandDecorator(
        Mock<ICacheService> cacheService,
        ITenantContext? tenantContext)
    {
        var inner = new Mock<ICommandHandler<CacheInvalidatingTestCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheInvalidatingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        return new CachingCommandDecorator<CacheInvalidatingTestCommand, Result>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingCommandDecorator<CacheInvalidatingTestCommand, Result>>.Instance,
            tenantContext)
        {
            ReInvalidationDelay = TimeSpan.Zero,
        };
    }
}
