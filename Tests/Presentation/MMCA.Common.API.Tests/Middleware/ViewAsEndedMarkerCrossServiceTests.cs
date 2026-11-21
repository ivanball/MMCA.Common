using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// The view-as "grant ended" marker (ADR-137) is written by the one service that holds the grant store
/// and read by every other service of the deployment. Each Store service sets its own
/// <c>Application:Namespace</c> and <c>Cache:KeyPrefix</c> over ONE Redis, so a marker under the writer's
/// namespace was never seen by the others, which kept serving the ended view-as token until it expired.
/// Each "service" here is its own container, wired the way a service host wires its cache, over one
/// shared L2.
/// </summary>
public sealed class ViewAsEndedMarkerCrossServiceTests
{
    private const string Audience = "MMCAStoreapi";

    // Not MemoryDistributedCache: HybridCache treats that one as no L2 at all, so nothing would be shared.
    private readonly SharedDistributedCache _sharedRedis = new();

    [Fact]
    public async Task EndedMarker_WrittenByTheGrantStoreService_IsReadByAnotherServiceOfTheSameDeployment()
    {
        await using var identity = BuildService("store-identity", Audience);
        await using var sales = BuildService("store-sales", Audience);
        var grantId = Guid.NewGuid();

        await ViewAsEndedMarker.WriteAsync(identity, ViewAsPrincipal(), grantId, TestContext.Current.CancellationToken);

        var marker = await sales.GetRequiredService<ICacheService>()
            .GetFromSharedStoreAsync<bool?>(ViewAsEndedMarker.KeyFor(grantId), TestContext.Current.CancellationToken);
        marker.Should().BeTrue("Sales must refuse a view-as token whose grant ended on Identity");
    }

    [Fact]
    public async Task EndedMarker_IsNotSeenByAnotherApplicationOnTheSameServer()
    {
        await using var store = BuildService("store-identity", Audience);
        await using var other = BuildService("adc", "ADCapi");
        var grantId = Guid.NewGuid();

        await ViewAsEndedMarker.WriteAsync(store, ViewAsPrincipal(), grantId, TestContext.Current.CancellationToken);

        (await other.GetRequiredService<ICacheService>().GetFromSharedStoreAsync<bool?>(ViewAsEndedMarker.KeyFor(grantId), TestContext.Current.CancellationToken))
            .Should().BeNull("another application's keyspace stays apart (SEC-Common-53)");
    }

    private static ClaimsPrincipal ViewAsPrincipal() =>
        new(new ClaimsIdentity(
            [new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))],
            "jwt"));

    private ServiceProvider BuildService(string applicationNamespace, string audience)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Application:Namespace"] = applicationNamespace,
            ["Cache:KeyPrefix"] = applicationNamespace + ":",
            ["Jwt:Audience"] = audience,
        }).Build();

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
