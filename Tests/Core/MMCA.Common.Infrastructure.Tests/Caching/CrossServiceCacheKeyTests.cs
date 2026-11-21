using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Infrastructure.Tests.Caching;

/// <summary>
/// Store Run 5 D1a: the services of one deployment each set their own <c>Application:Namespace</c> and
/// <c>Cache:KeyPrefix</c> over ONE Redis, so the soft-deleted-user marker Identity wrote under its own
/// namespace was never read by Sales, which kept answering 200 to a locked user's token. Each "service"
/// here is its own container, wired the way a service host wires its cache, over one shared L2.
/// </summary>
public sealed class CrossServiceCacheKeyTests
{
    private const string Audience = "MMCAStoreapi";

    // Not MemoryDistributedCache: HybridCache treats that one as no L2 at all, so nothing would be shared.
    private readonly SharedDistributedCache _sharedRedis = new();

    [Fact]
    public async Task SoftDeletedMarker_WrittenByOneService_IsReadByAnotherServiceOfTheSameDeployment()
    {
        await using var identity = BuildService("store-identity", Audience);
        await using var sales = BuildService("store-sales", Audience);

        await SoftDeletedUserCache.MarkDeletedAsync(identity.GetRequiredService<ICacheService>(), 42, TestContext.Current.CancellationToken);

        var marker = await sales.GetRequiredService<ICacheService>()
            .GetFromSharedStoreAsync<bool?>(SoftDeletedUserCache.KeyFor(42), TestContext.Current.CancellationToken);
        marker.Should().BeTrue("Sales must refuse the token of a user Identity locked or deleted");
    }

    [Fact]
    public async Task SoftDeletedMarker_ClearedByOneService_IsGoneForAnotherService()
    {
        await using var identity = BuildService("store-identity", Audience);
        await using var sales = BuildService("store-sales", Audience);
        var identityCache = identity.GetRequiredService<ICacheService>();
        await SoftDeletedUserCache.MarkDeletedAsync(identityCache, 42, TestContext.Current.CancellationToken);

        await SoftDeletedUserCache.ClearMarkerAsync(identityCache, 42, TestContext.Current.CancellationToken);

        var marker = await sales.GetRequiredService<ICacheService>()
            .GetFromSharedStoreAsync<bool?>(SoftDeletedUserCache.KeyFor(42), TestContext.Current.CancellationToken);
        marker.Should().BeNull("an unlock on Identity must reach every service at once");
    }

    [Fact]
    public async Task SoftDeletedMarker_SurvivesAPrefixWipeInTheReadingService()
    {
        await using var identity = BuildService("store-identity", Audience);
        await using var sales = BuildService("store-sales", Audience);
        var salesCache = sales.GetRequiredService<ICacheService>();
        await SoftDeletedUserCache.MarkDeletedAsync(identity.GetRequiredService<ICacheService>(), 42, TestContext.Current.CancellationToken);

        await salesCache.RemoveByPrefixAsync(string.Empty, TestContext.Current.CancellationToken);

        (await salesCache.GetFromSharedStoreAsync<bool?>(SoftDeletedUserCache.KeyFor(42), TestContext.Current.CancellationToken))
            .Should().BeTrue("a cross-service key carries no per-service generation token");
    }

    [Fact]
    public async Task OrdinaryKeys_StayInEachServicesOwnNamespace()
    {
        await using var identity = BuildService("store-identity", Audience);
        await using var sales = BuildService("store-sales", Audience);

        await identity.GetRequiredService<ICacheService>().SetAsync("catalog:1", "identity-value", cancellationToken: TestContext.Current.CancellationToken);

        (await sales.GetRequiredService<ICacheService>().GetFromSharedStoreAsync<string>("catalog:1", TestContext.Current.CancellationToken))
            .Should().BeNull("only cross-service keys leave the per-service namespace");
    }

    [Fact]
    public async Task SoftDeletedMarker_IsNotSeenByAnotherApplicationOnTheSameServer()
    {
        await using var store = BuildService("store-identity", Audience);
        await using var other = BuildService("adc", "ADCapi");

        await SoftDeletedUserCache.MarkDeletedAsync(store.GetRequiredService<ICacheService>(), 42, TestContext.Current.CancellationToken);

        (await other.GetRequiredService<ICacheService>().GetFromSharedStoreAsync<bool?>(SoftDeletedUserCache.KeyFor(42), TestContext.Current.CancellationToken))
            .Should().BeNull("user 42 of another application is a different user (SEC-Common-53)");
    }

    [Fact]
    public async Task SoftDeletedMarker_HonorsAnExplicitCrossServicePrefix()
    {
        await using var identity = BuildService("store-identity", audience: null, crossServicePrefix: "store-shared:");
        await using var sales = BuildService("store-sales", audience: null, crossServicePrefix: "store-shared:");

        await SoftDeletedUserCache.MarkDeletedAsync(identity.GetRequiredService<ICacheService>(), 42, TestContext.Current.CancellationToken);

        (await sales.GetRequiredService<ICacheService>().GetFromSharedStoreAsync<bool?>(SoftDeletedUserCache.KeyFor(42), TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    private ServiceProvider BuildService(string applicationNamespace, string? audience, string? crossServicePrefix = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Application:Namespace"] = applicationNamespace,
            ["Cache:KeyPrefix"] = applicationNamespace + ":",
        };
        if (audience is not null)
        {
            settings["Jwt:Audience"] = audience;
        }

        if (crossServicePrefix is not null)
        {
            settings["Cache:CrossServiceKeyPrefix"] = crossServicePrefix;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDistributedCache>(_sharedRedis);
        services.AddCaching(configuration);
        services.AddCommonHybridCache();
        return services.BuildServiceProvider();
    }

    /// <summary>One in-memory store standing in for the Redis every service of the deployment shares.</summary>
    private sealed class SharedDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => _store.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _store.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
