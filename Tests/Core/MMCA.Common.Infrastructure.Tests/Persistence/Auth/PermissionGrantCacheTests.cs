using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// The in-memory permission-grant snapshot: role names match case-insensitively on read (M109), and
/// an entry outlives the gap before the next rebuild while still expiring when refreshes keep failing
/// (M111).
/// </summary>
public sealed class PermissionGrantCacheTests : IDisposable
{
    private const string SessionsRead = "sessions:read";

    private readonly ManualClock _clock = new();
    private readonly MemoryCache _memoryCache;
    private readonly ServiceProvider _provider;

    public PermissionGrantCacheTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });

        var store = new Mock<IPermissionGrantStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([PermissionGrant.Create("manager", SessionsRead, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Value!]);

        var services = new ServiceCollection();
        services.AddScoped(_ => store.Object);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetPermissions_WithADifferentlyCasedRole_ReturnsTheStoredGrants()
    {
        using var sut = CreateSut(cacheSeconds: 300);

        await sut.RefreshAsync(CancellationToken.None);

        sut.GetPermissions("Manager").Should().Contain(SessionsRead);
        sut.GetPermissions("MANAGER").Should().Contain(SessionsRead);
    }

    [Fact]
    public async Task GetPermissions_AfterOneIntervalWithoutARebuild_StillServesTheSnapshotUntilThreeIntervals()
    {
        using var sut = CreateSut(cacheSeconds: 5);

        await sut.RefreshAsync(CancellationToken.None);

        _clock.UtcNow += TimeSpan.FromSeconds(6);
        sut.GetPermissions("manager").Should().Contain(
            SessionsRead,
            "the next rebuild starts one interval after this one finished, so the entry must outlive that gap");

        _clock.UtcNow += TimeSpan.FromSeconds(10);
        sut.GetPermissions("manager").Should().BeEmpty(
            "past three intervals without a successful rebuild, stored grants fail closed");
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        _provider.Dispose();
    }

    private PermissionGrantCache CreateSut(int cacheSeconds) =>
        new(
            _memoryCache,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PermissionGrantSettings { CacheSeconds = cacheSeconds }));

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
