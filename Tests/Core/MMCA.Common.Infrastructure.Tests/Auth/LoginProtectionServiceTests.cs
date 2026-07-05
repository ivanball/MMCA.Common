using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies the ADR-029 brute-force protections: lockout checks, the exponential-backoff
/// lockout math (including the <c>MaxLockoutSeconds</c> cap), the TTL written per attempt,
/// reset behavior, and the per-IP registration window.
/// </summary>
public sealed class LoginProtectionServiceTests
{
    private const string TestEmail = "user@example.com";
    private const string AttemptsKey = $"login:attempts:{TestEmail}";
    private const string LockoutKey = $"login:lockout:{TestEmail}";
    private const string TestIp = "203.0.113.7";
    private const string RegistrationKey = $"registration:ip:{TestIp}";

    // ── Lockout check ──
    [Fact]
    public async Task CheckLockoutAsync_WhenNoLockoutRecorded_ReturnsSuccess()
    {
        var (sut, _) = CreateSut();

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CheckLockoutAsync_WhenLockedOut_ReturnsUnauthorizedFailure()
    {
        var (sut, cache) = CreateSut();
        cache.Seed(LockoutKey, true);

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.TooManyAttempts" && e.Type == ErrorType.Unauthorized);
    }

    // ── Failed-attempt counting ──
    [Fact]
    public async Task IncrementFailedAttemptsAsync_FirstAttempt_WritesCountOfOneWithWindowTtl()
    {
        var (sut, cache) = CreateSut(failedAttemptWindowMinutes: 30);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[AttemptsKey].Should().Be(1);
        cache.Ttls[AttemptsKey].Should().Be(TimeSpan.FromMinutes(30));
        cache.Values.Should().NotContainKey(LockoutKey, "below the threshold no lockout is applied");
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_BelowThreshold_DoesNotApplyLockout()
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5);
        cache.Seed(AttemptsKey, 3);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[AttemptsKey].Should().Be(4);
        cache.Values.Should().NotContainKey(LockoutKey);
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_AtThreshold_AppliesOneSecondLockout()
    {
        // newCount == MaxFailedAttempts means zero excess attempts: min(1 << 0, cap) == 1 second.
        var (sut, cache) = CreateSut(maxFailedAttempts: 5, maxLockoutSeconds: 300);
        cache.Seed(AttemptsKey, 4);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[LockoutKey].Should().Be(true);
        cache.Ttls[LockoutKey].Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(4, 1)] // newCount 5, excess 0: 1 << 0 = 1s
    [InlineData(5, 2)] // newCount 6, excess 1: 1 << 1 = 2s
    [InlineData(6, 4)] // newCount 7, excess 2: 1 << 2 = 4s
    [InlineData(7, 8)] // newCount 8, excess 3: 1 << 3 = 8s
    [InlineData(12, 256)] // newCount 13, excess 8: 1 << 8 = 256s (last value under the cap)
    [InlineData(13, 300)] // newCount 14, excess 9: 1 << 9 = 512s, capped to MaxLockoutSeconds
    [InlineData(20, 300)] // deep excess stays pinned at the cap
    public async Task IncrementFailedAttemptsAsync_ExponentialBackoff_MatchesMinOfShiftAndCap(
        int previousAttempts,
        int expectedLockoutSeconds)
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5, maxLockoutSeconds: 300);
        cache.Seed(AttemptsKey, previousAttempts);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[LockoutKey].Should().Be(true);
        cache.Ttls[LockoutKey].Should().Be(TimeSpan.FromSeconds(expectedLockoutSeconds));
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_EveryAttempt_RefreshesAttemptWindowTtl()
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5, failedAttemptWindowMinutes: 15);
        cache.Seed(AttemptsKey, 7);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[AttemptsKey].Should().Be(8);
        cache.Ttls[AttemptsKey].Should().Be(TimeSpan.FromMinutes(15));
    }

    // ── Reset ──
    [Fact]
    public async Task ResetFailedAttemptsAsync_RemovesAttemptCounterAndLockout()
    {
        var (sut, cache) = CreateSut();
        cache.Seed(AttemptsKey, 6);
        cache.Seed(LockoutKey, true);

        await sut.ResetFailedAttemptsAsync(TestEmail);

        cache.Values.Should().NotContainKey(AttemptsKey);
        cache.Values.Should().NotContainKey(LockoutKey);
    }

    // ── Registration rate limit ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CheckRegistrationRateLimitAsync_WhenIpMissing_ReturnsSuccess(string? ipAddress)
    {
        var (sut, _) = CreateSut();

        Result result = await sut.CheckRegistrationRateLimitAsync(ipAddress);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CheckRegistrationRateLimitAsync_BelowLimit_ReturnsSuccess()
    {
        var (sut, cache) = CreateSut(maxRegistrationsPerIpPerHour: 10);
        cache.Seed(RegistrationKey, 9);

        Result result = await sut.CheckRegistrationRateLimitAsync(TestIp);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CheckRegistrationRateLimitAsync_AtLimit_ReturnsUnauthorizedFailure()
    {
        var (sut, cache) = CreateSut(maxRegistrationsPerIpPerHour: 10);
        cache.Seed(RegistrationKey, 10);

        Result result = await sut.CheckRegistrationRateLimitAsync(TestIp);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.RegistrationRateLimitExceeded" && e.Type == ErrorType.Unauthorized);
    }

    // ── Registration counting ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task IncrementRegistrationCountAsync_WhenIpMissing_WritesNothing(string? ipAddress)
    {
        var (sut, cache) = CreateSut();

        await sut.IncrementRegistrationCountAsync(ipAddress);

        cache.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementRegistrationCountAsync_FirstRegistration_WritesCountOfOneWithWindowTtl()
    {
        var (sut, cache) = CreateSut(registrationRateLimitWindowMinutes: 60);

        await sut.IncrementRegistrationCountAsync(TestIp);

        cache.Values[RegistrationKey].Should().Be(1);
        cache.Ttls[RegistrationKey].Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task IncrementRegistrationCountAsync_ExistingCount_IncrementsAndRefreshesWindow()
    {
        var (sut, cache) = CreateSut(registrationRateLimitWindowMinutes: 45);
        cache.Seed(RegistrationKey, 2);

        await sut.IncrementRegistrationCountAsync(TestIp);

        cache.Values[RegistrationKey].Should().Be(3);
        cache.Ttls[RegistrationKey].Should().Be(TimeSpan.FromMinutes(45));
    }

    // ── Helpers ──
    private static (LoginProtectionService Sut, FakeCacheService Cache) CreateSut(
        int maxFailedAttempts = 5,
        int maxLockoutSeconds = 300,
        int failedAttemptWindowMinutes = 30,
        int maxRegistrationsPerIpPerHour = 10,
        int registrationRateLimitWindowMinutes = 60)
    {
        var cache = new FakeCacheService();
        var settings = new LoginProtectionSettings
        {
            MaxFailedAttempts = maxFailedAttempts,
            MaxLockoutSeconds = maxLockoutSeconds,
            FailedAttemptWindowMinutes = failedAttemptWindowMinutes,
            MaxRegistrationsPerIpPerHour = maxRegistrationsPerIpPerHour,
            RegistrationRateLimitWindowMinutes = registrationRateLimitWindowMinutes,
        };
        var sut = new LoginProtectionService(cache, Options.Create(settings));

        return (sut, cache);
    }

    /// <summary>In-memory <see cref="ICacheService"/> recording every value and TTL written.</summary>
    private sealed class FakeCacheService : ICacheService
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TimeSpan?> Ttls { get; } = new(StringComparer.Ordinal);

        public void Seed(string key, object? value) => Values[key] = value;

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out object? value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            Ttls[key] = expiration;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            Ttls.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            foreach (string key in Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                Values.Remove(key);
                Ttls.Remove(key);
            }

            return Task.CompletedTask;
        }
    }
}
