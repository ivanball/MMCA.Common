using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using Moq;

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
    public async Task CheckLockoutAsync_WhenLockedOut_ReturnsTooManyRequestsFailure()
    {
        var (sut, cache) = CreateSut();
        cache.Seed(LockoutKey, true);

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.TooManyAttempts" && e.Type == ErrorType.TooManyRequests);
    }

    // A reset removes the lockout from the shared store and from THIS replica's local copy only, so a
    // lockout read through the local tier stayed live on every other replica for up to its L1 lifetime.
    [Fact]
    public async Task CheckLockoutAsync_ReadsTheLockoutFromTheSharedStore_NeverFromALocalCopy()
    {
        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.GetFromSharedStoreAsync<bool?>(LockoutKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = new LoginProtectionService(cache.Object, Options.Create(new LoginProtectionSettings()));

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsFailure.Should().BeTrue("the shared store holds the lockout");
        cache.Verify(c => c.GetFromSharedStoreAsync<bool?>(LockoutKey, It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(c => c.GetAsync<bool?>(LockoutKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckLockoutAsync_WhenOnlyAStaleLocalCopySaysLocked_Succeeds()
    {
        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.GetAsync<bool?>(LockoutKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        cache.Setup(c => c.GetFromSharedStoreAsync<bool?>(LockoutKey, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)null);
        var sut = new LoginProtectionService(cache.Object, Options.Create(new LoginProtectionSettings()));

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsSuccess.Should().BeTrue("a reset on another replica cleared the shared lockout");
    }

    // ── Failed-attempt counting ──
    [Fact]
    public async Task IncrementFailedAttemptsAsync_FirstAttempt_WritesCountOfOneWithWindowTtl()
    {
        var (sut, cache) = CreateSut(failedAttemptWindowMinutes: 30);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[AttemptsKey].Should().Be(1L);
        cache.Ttls[AttemptsKey].Should().Be(TimeSpan.FromMinutes(30));
        cache.Values.Should().NotContainKey(LockoutKey, "below the threshold no lockout is applied");
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_BelowThreshold_DoesNotApplyLockout()
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5);
        cache.Seed(AttemptsKey, 3L);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[AttemptsKey].Should().Be(4L);
        cache.Values.Should().NotContainKey(LockoutKey);
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_AtThreshold_AppliesOneSecondLockout()
    {
        // newCount == MaxFailedAttempts means zero excess attempts: min(1 << 0, cap) == 1 second.
        var (sut, cache) = CreateSut(maxFailedAttempts: 5, maxLockoutSeconds: 300);
        cache.Seed(AttemptsKey, 4L);

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
    [InlineData(35, 300)] // excess 31: unclamped 1 << 31 is negative; the clamped exponent keeps the cap
    [InlineData(36, 300)] // excess 32: unclamped shift masks to 1 << 0 = 1s; must stay at the cap
    [InlineData(40, 300)] // excess 36: unclamped shift masks to 1 << 4 = 16s; must stay at the cap
    public async Task IncrementFailedAttemptsAsync_ExponentialBackoff_MatchesMinOfShiftAndCap(
        int previousAttempts,
        int expectedLockoutSeconds)
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5, maxLockoutSeconds: 300);
        cache.Seed(AttemptsKey, (long)previousAttempts);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[LockoutKey].Should().Be(true);
        cache.Ttls[LockoutKey].Should().Be(TimeSpan.FromSeconds(expectedLockoutSeconds));
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_EveryAttempt_RefreshesAttemptWindowTtl()
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5, failedAttemptWindowMinutes: 15);
        cache.Seed(AttemptsKey, 7L);

        await sut.IncrementFailedAttemptsAsync(TestEmail);

        cache.Values[AttemptsKey].Should().Be(8L);
        cache.Ttls[AttemptsKey].Should().Be(TimeSpan.FromMinutes(15));
    }

    // ── Reset ──
    [Fact]
    public async Task ResetFailedAttemptsAsync_RemovesAttemptCounterAndLockout()
    {
        var (sut, cache) = CreateSut();
        cache.Seed(AttemptsKey, 6L);
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
        cache.Seed(RegistrationKey, 9L);

        Result result = await sut.CheckRegistrationRateLimitAsync(TestIp);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CheckRegistrationRateLimitAsync_AtLimit_ReturnsUnauthorizedFailure()
    {
        var (sut, cache) = CreateSut(maxRegistrationsPerIpPerHour: 10);
        cache.Seed(RegistrationKey, 10L);

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

        cache.Values[RegistrationKey].Should().Be(1L);
        cache.Ttls[RegistrationKey].Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task IncrementRegistrationCountAsync_ExistingCount_IncrementsAndRefreshesWindow()
    {
        var (sut, cache) = CreateSut(registrationRateLimitWindowMinutes: 45);
        cache.Seed(RegistrationKey, 2L);

        await sut.IncrementRegistrationCountAsync(TestIp);

        cache.Values[RegistrationKey].Should().Be(3L);
        cache.Ttls[RegistrationKey].Should().Be(TimeSpan.FromMinutes(45));
    }

    // ── Identity normalization ──
    // The counters must key off the same normalized address the user lookup resolves against.
    // Keying off raw request input let an attacker reset the ADR-029 backoff by varying case or
    // padding the address with whitespace: every variant got its own counter and lockout while
    // still targeting one account.
    [Theory]
    [InlineData("User@Example.com")]
    [InlineData("USER@EXAMPLE.COM")]
    [InlineData("  user@example.com  ")]
    public async Task IncrementFailedAttemptsAsync_EmailVariants_ShareOneAttemptCounter(string variant)
    {
        var (sut, cache) = CreateSut(maxFailedAttempts: 5);
        cache.Seed(AttemptsKey, 3L);

        await sut.IncrementFailedAttemptsAsync(variant);

        cache.Values[AttemptsKey].Should().Be(4L, "the variant must not get its own counter");
        cache.Values.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("User@Example.com")]
    [InlineData("USER@EXAMPLE.COM")]
    [InlineData("  user@example.com  ")]
    public async Task CheckLockoutAsync_EmailVariants_SeeTheSameLockout(string variant)
    {
        var (sut, cache) = CreateSut();
        cache.Seed(LockoutKey, true);

        Result result = await sut.CheckLockoutAsync(variant);

        result.IsFailure.Should().BeTrue("a lockout recorded for the normalized address applies to every variant");
    }

    [Fact]
    public async Task ResetFailedAttemptsAsync_EmailVariant_ClearsTheNormalizedKeys()
    {
        var (sut, cache) = CreateSut();
        cache.Seed(AttemptsKey, 6L);
        cache.Seed(LockoutKey, true);

        await sut.ResetFailedAttemptsAsync("User@Example.COM");

        cache.Values.Should().NotContainKey(AttemptsKey);
        cache.Values.Should().NotContainKey(LockoutKey);
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_MalformedEmail_StillCollapsesVariantsOntoOneKey()
    {
        // A malformed address never matches a user, but it still increments a counter; the
        // trim-and-lowercase fallback keeps its variants on one key rather than one key each.
        var (sut, cache) = CreateSut();

        await sut.IncrementFailedAttemptsAsync("Not-An-Email");
        await sut.IncrementFailedAttemptsAsync("  not-an-email  ");

        cache.Values["login:attempts:not-an-email"].Should().Be(2L);
        cache.Values.Should().HaveCount(1);
    }

    // ── Cache outage: fail open (CacheSettings: a cache outage never becomes an error) ──
    [Fact]
    public async Task CheckLockoutAsync_WhenTheCacheIsDown_ReturnsSuccess()
    {
        var sut = CreateOutageSut(out _);

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsSuccess.Should().BeTrue("an unreachable cache holds no lockout, so sign-in proceeds");
    }

    [Fact]
    public async Task CheckRegistrationRateLimitAsync_WhenTheCacheIsDown_ReturnsSuccess()
    {
        var sut = CreateOutageSut(out _);

        Result result = await sut.CheckRegistrationRateLimitAsync(TestIp);

        result.IsSuccess.Should().BeTrue("an unreachable cache holds no registration count, so registration proceeds");
    }

    [Fact]
    public async Task IncrementFailedAttemptsAsync_WhenTheCacheIsDown_CompletesWithoutThrowing()
    {
        var sut = CreateOutageSut(out _);

        Func<Task> act = () => sut.IncrementFailedAttemptsAsync(TestEmail);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ResetFailedAttemptsAsync_WhenTheCacheIsDown_CompletesWithoutThrowing()
    {
        var sut = CreateOutageSut(out _);

        Func<Task> act = () => sut.ResetFailedAttemptsAsync(TestEmail);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IncrementRegistrationCountAsync_WhenTheCacheIsDown_CompletesWithoutThrowing()
    {
        var sut = CreateOutageSut(out _);

        Func<Task> act = () => sut.IncrementRegistrationCountAsync(TestIp);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckLockoutAsync_WhenTheCacheIsDown_LogsAWarningWithoutTheEmail()
    {
        var sut = CreateOutageSut(out List<(LogLevel Level, string Message)> logged);

        await sut.CheckLockoutAsync(TestEmail);

        var entry = logged.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain(nameof(LoginProtectionService.CheckLockoutAsync));
        entry.Message.Should().NotContain(TestEmail, "the cache keys carry personal data and stay out of the log");
    }

    [Fact]
    public async Task CheckLockoutAsync_WhenTheStoreCancelsOnItsOwn_TreatsItAsAnOutage()
    {
        // A store-side timeout surfaces as OperationCanceledException with the caller's token live.
        var sut = new LoginProtectionService(
            new ThrowingCacheService(new OperationCanceledException("store timeout")),
            Options.Create(new LoginProtectionSettings()));

        Result result = await sut.CheckLockoutAsync(TestEmail);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(LoginProtectionService.CheckLockoutAsync))]
    [InlineData(nameof(LoginProtectionService.IncrementFailedAttemptsAsync))]
    [InlineData(nameof(LoginProtectionService.ResetFailedAttemptsAsync))]
    [InlineData(nameof(LoginProtectionService.CheckRegistrationRateLimitAsync))]
    [InlineData(nameof(LoginProtectionService.IncrementRegistrationCountAsync))]
    public async Task EveryMethod_WhenTheCallerCancels_StillThrowsOperationCanceled(string method)
    {
        var sut = CreateOutageSut(out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = method switch
        {
            nameof(LoginProtectionService.CheckLockoutAsync) => () => sut.CheckLockoutAsync(TestEmail, cts.Token),
            nameof(LoginProtectionService.IncrementFailedAttemptsAsync) => () => sut.IncrementFailedAttemptsAsync(TestEmail, cts.Token),
            nameof(LoginProtectionService.ResetFailedAttemptsAsync) => () => sut.ResetFailedAttemptsAsync(TestEmail, cts.Token),
            nameof(LoginProtectionService.CheckRegistrationRateLimitAsync) => () => sut.CheckRegistrationRateLimitAsync(TestIp, cts.Token),
            _ => () => sut.IncrementRegistrationCountAsync(TestIp, cts.Token),
        };

        await act.Should().ThrowAsync<OperationCanceledException>("the caller's own cancellation is never swallowed");
    }

    // ── Helpers ──
    private static LoginProtectionService CreateOutageSut(out List<(LogLevel Level, string Message)> logged)
    {
        var sink = new List<(LogLevel Level, string Message)>();
        logged = sink;
        var logger = new Mock<ILogger<LoginProtectionService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var formatter = (Delegate)invocation.Arguments[4]!;
                sink.Add((
                    (LogLevel)invocation.Arguments[0]!,
                    (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!));
            }));

        return new LoginProtectionService(
            new ThrowingCacheService(new InvalidOperationException("It was not possible to connect to the redis server(s).")),
            Options.Create(new LoginProtectionSettings()),
            logger.Object);
    }
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

    /// <summary>
    /// An <see cref="ICacheService"/> whose every member fails the way an unreachable store does,
    /// including the members the interface implements by default, after first honoring the caller's
    /// token exactly as a real store would.
    /// </summary>
    private sealed class ThrowingCacheService(Exception fault) : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Fail<T?>(cancellationToken);

        public Task<T?> GetFromSharedStoreAsync<T>(string key, CancellationToken cancellationToken = default) => Fail<T?>(cancellationToken);

        public Task<(bool Found, T? Value)> TryGetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Fail<(bool Found, T? Value)>(cancellationToken);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default) =>
            Fail<bool>(cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Fail<bool>(cancellationToken);

        public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Fail<bool>(cancellationToken);

        public Task<long> IncrementAsync(string key, TimeSpan expiration, CancellationToken cancellationToken = default) =>
            Fail<long>(cancellationToken);

        private Task<TResult> Fail<TResult>(CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<TResult>(cancellationToken)
                : Task.FromException<TResult>(fault);
    }
}
