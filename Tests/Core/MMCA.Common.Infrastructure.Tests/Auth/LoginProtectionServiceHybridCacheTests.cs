using System.Collections.Concurrent;
using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Runs <see cref="LoginProtectionService"/> over the REAL two-level cache that
/// <c>AddCommonHybridCache</c> registers (an in-process L1 in front of a shared
/// <see cref="IDistributedCache"/> L2), with the default
/// <see cref="LoginProtectionSettings"/>. The unit tests in <see cref="LoginProtectionServiceTests"/>
/// use a single-level fake, so they cannot see a counter whose read and write legs land on
/// different tiers: the write goes to the shared store while the read is answered from a local copy.
/// </summary>
public sealed class LoginProtectionServiceHybridCacheTests
{
    private const string TestIp = "203.0.113.7";
    private const string TestEmail = "user@example.com";

    [Fact]
    public async Task RegistrationBurst_FromOneIp_OverTheHybridCache_RefusesTheRegistrationPastTheHourlyLimit()
    {
        await using var provider = BuildProvider();
        var sut = CreateSut(provider);
        var settings = new LoginProtectionSettings();
        var cancellationToken = TestContext.Current.CancellationToken;

        // The registration flow: check the limit, then record the registration. The first
        // MaxRegistrationsPerIpPerHour registrations are allowed.
        for (var registration = 1; registration <= settings.MaxRegistrationsPerIpPerHour; registration++)
        {
            Result allowed = await sut.CheckRegistrationRateLimitAsync(TestIp, cancellationToken);
            allowed.IsSuccess.Should().BeTrue(string.Create(CultureInfo.InvariantCulture, $"registration {registration} is within the limit of {settings.MaxRegistrationsPerIpPerHour}"));
            await sut.IncrementRegistrationCountAsync(TestIp, cancellationToken);
        }

        Result refused = await sut.CheckRegistrationRateLimitAsync(TestIp, cancellationToken);

        refused.IsFailure.Should().BeTrue(
            string.Create(CultureInfo.InvariantCulture, $"{settings.MaxRegistrationsPerIpPerHour} registrations from one IP were already recorded inside the window, so registration {settings.MaxRegistrationsPerIpPerHour + 1} must be refused; a check answered from a stale in-process copy of the counter never sees the shared count grow"));
        refused.Errors.Should().ContainSingle(e => e.Code == "Auth.RegistrationRateLimitExceeded");
    }

    [Fact]
    public async Task FailedLoginBurst_OverTheHybridCache_LocksTheAccountAfterMaxFailedAttempts()
    {
        await using var provider = BuildProvider();
        var sut = CreateSut(provider);
        var settings = new LoginProtectionSettings();
        var cancellationToken = TestContext.Current.CancellationToken;

        // The sign-in flow: check the lockout, then record the failed attempt. The first
        // MaxFailedAttempts attempts are let through to the password check.
        for (var attempt = 1; attempt <= settings.MaxFailedAttempts; attempt++)
        {
            Result allowed = await sut.CheckLockoutAsync(TestEmail, cancellationToken);
            allowed.IsSuccess.Should().BeTrue(string.Create(CultureInfo.InvariantCulture, $"attempt {attempt} is within the {settings.MaxFailedAttempts} permitted failures"));
            await sut.IncrementFailedAttemptsAsync(TestEmail, cancellationToken);
        }

        Result locked = await sut.CheckLockoutAsync(TestEmail, cancellationToken);

        locked.IsFailure.Should().BeTrue(
            string.Create(CultureInfo.InvariantCulture, $"{settings.MaxFailedAttempts} consecutive failures were recorded, so the next attempt must be locked out"));
        locked.Errors.Should().ContainSingle(e => e.Code == "Auth.TooManyAttempts");
    }

    /// <summary>
    /// The production two-level registration: <see cref="SharedStore"/> stands in for Redis as the
    /// shared L2, and <c>AddCommonHybridCache</c> supplies the L1, the entry policy and the
    /// <see cref="ICacheService"/>.
    /// <para>
    /// Not <c>AddDistributedMemoryCache</c>: <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/>
    /// recognizes <c>MemoryDistributedCache</c> and runs with NO L2 at all, so a counter that bypasses
    /// L1 would never be stored and every increment would read zero.
    /// </para>
    /// </summary>
    /// <returns>The provider owning the cache.</returns>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(new SharedStore());
        services.AddCommonHybridCache();
        return services.BuildServiceProvider();
    }

    private static LoginProtectionService CreateSut(ServiceProvider provider) =>
        new(provider.GetRequiredService<ICacheService>(), Options.Create(new LoginProtectionSettings()));

    /// <summary>In-memory <see cref="IDistributedCache"/> playing the shared store (Redis) every replica reads.</summary>
    private sealed class SharedStore : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => _store.TryGetValue(key, out var bytes) ? bytes : null;

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
