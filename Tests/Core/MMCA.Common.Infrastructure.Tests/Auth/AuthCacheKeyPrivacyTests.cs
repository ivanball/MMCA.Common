using System.Collections.Concurrent;
using System.Globalization;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Concurrency;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Pins the privacy requirement on the auth services' cache keys: a user's email address never
/// appears in a key that <see cref="LoginProtectionService"/>, <see cref="PasswordResetTokenService"/>
/// or <see cref="EmailConfirmationTokenService"/> reads, writes or removes, so it can never reach a
/// Redis key name, a log line or an exception message. Each key still carries its own prefix, and
/// two spellings of one address still land on one key, but nothing here assumes how the address is
/// reduced to the part after the prefix.
/// </summary>
public sealed class AuthCacheKeyPrivacyTests
{
    private const string TestEmail = "Privacy.Probe@Example.com";
    private const string LocalPart = "Privacy.Probe";
    private const UserIdentifierType TestUserId = 42;

    private static readonly string[] LoginPrefixes = ["login:lockout:", "login:attempts:"];
    private static readonly string[] PasswordResetPrefixes = ["pwdreset:token:", "pwdreset:req:"];
    private static readonly string[] EmailConfirmationPrefixes = ["emailconfirm:token:", "emailconfirm:req:"];

    // ── A: no key carries the address ──
    [Fact]
    public async Task LoginProtection_FailedLoginsPastTheLockout_NeverPutTheAddressInACacheKey()
    {
        var cache = new RecordingCacheService();

        await RunLoginFlowAsync(cache, TestEmail, TestContext.Current.CancellationToken);

        AssertKeysArePseudonymous(cache.Keys, LoginPrefixes);
    }

    [Fact]
    public async Task PasswordReset_IssueAndRedeem_NeverPutTheAddressInACacheKeyOrLockName()
    {
        var cache = new RecordingCacheService();
        var distributedLock = new RecordingDistributedLock();

        await RunPasswordResetFlowAsync(cache, distributedLock, TestEmail, TestContext.Current.CancellationToken);

        AssertKeysArePseudonymous(cache.Keys, PasswordResetPrefixes);
        distributedLock.Keys.Should().NotBeEmpty("a redemption serializes on a lock named after the token key");
        distributedLock.Keys.Where(MentionsAddress).Should().BeEmpty(
            "the redemption lock is a Redis key too, so its name must not carry the address either");
    }

    [Fact]
    public async Task EmailConfirmation_IssueAndRedeem_NeverPutTheAddressInACacheKey()
    {
        var cache = new RecordingCacheService();

        await RunEmailConfirmationFlowAsync(cache, TestEmail, TestContext.Current.CancellationToken);

        AssertKeysArePseudonymous(cache.Keys, EmailConfirmationPrefixes);
    }

    // ── B: normalization still collapses spellings onto one key ──
    [Fact]
    public async Task LoginProtection_TwoSpellingsOfOneAddress_ShareEachKey()
    {
        var cache = new RecordingCacheService();
        var cancellationToken = TestContext.Current.CancellationToken;

        await RunLoginFlowAsync(cache, " Privacy.Probe@Example.COM ", cancellationToken);
        await RunLoginFlowAsync(cache, "privacy.probe@example.com", cancellationToken);

        AssertOneKeyPerPrefix(cache.Keys, LoginPrefixes);
    }

    [Fact]
    public async Task PasswordReset_TwoSpellingsOfOneAddress_ShareEachKey()
    {
        var cache = new RecordingCacheService();
        var cancellationToken = TestContext.Current.CancellationToken;

        await RunPasswordResetFlowAsync(cache, new RecordingDistributedLock(), " Privacy.Probe@Example.COM ", cancellationToken);
        await RunPasswordResetFlowAsync(cache, new RecordingDistributedLock(), "privacy.probe@example.com", cancellationToken);

        AssertOneKeyPerPrefix(cache.Keys, PasswordResetPrefixes);
    }

    [Fact]
    public async Task EmailConfirmation_TwoSpellingsOfOneAddress_ShareEachKey()
    {
        var cache = new RecordingCacheService();
        var cancellationToken = TestContext.Current.CancellationToken;

        await RunEmailConfirmationFlowAsync(cache, " Privacy.Probe@Example.COM ", cancellationToken);
        await RunEmailConfirmationFlowAsync(cache, "privacy.probe@example.com", cancellationToken);

        AssertOneKeyPerPrefix(cache.Keys, EmailConfirmationPrefixes);
    }

    // ── C: a cache read fault on the real two-level cache leaks nothing ──
    [Fact]
    public async Task LoginProtection_OverTheHybridCache_WhenSharedStoreReadsFault_LogsNoAddress()
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddSingleton<IDistributedCache>(new ReadFaultingStore());
        services.AddCommonHybridCache();
        await using var provider = services.BuildServiceProvider();
        var sut = new LoginProtectionService(
            provider.GetRequiredService<ICacheService>(),
            Options.Create(new LoginProtectionSettings()),
            provider.GetRequiredService<ILogger<LoginProtectionService>>());
        var settings = new LoginProtectionSettings();
        var cancellationToken = TestContext.Current.CancellationToken;

        for (var attempt = 1; attempt <= settings.MaxFailedAttempts + 1; attempt++)
        {
            await sut.CheckLockoutAsync(TestEmail, cancellationToken);
            await sut.IncrementFailedAttemptsAsync(TestEmail, cancellationToken);
        }

        var captured = logs.Entries.ToList();
        captured.Should().Contain(
            e => e.Message.Contains("Two-level cache read failed", StringComparison.Ordinal),
            "the lockout check after the lockout is written reads back a flag this build cannot deserialize");

        // One scope so a failing run names every leak path at once, not only the first.
        using var scope = new AssertionScope();
        captured.Where(e => MentionsAddress(e.Message)).Select(e => e.Message).Should().BeEmpty(
            "no formatted log message may carry the address");
        captured.SelectMany(e => e.StateValues).Where(MentionsAddress).Should().BeEmpty(
            "no structured log value may carry the address, since sinks index those separately");
        captured.Where(e => e.Exception is not null).Select(e => e.Exception!.ToString()).Where(MentionsAddress).Should().BeEmpty(
            "a store fault that quotes the key it failed on (as StackExchange.Redis does) must not carry the address");
    }

    // ── Flows ──
    private static async Task RunLoginFlowAsync(ICacheService cache, string email, CancellationToken cancellationToken)
    {
        var settings = new LoginProtectionSettings();
        var sut = new LoginProtectionService(cache, Options.Create(settings));

        for (var attempt = 1; attempt <= settings.MaxFailedAttempts; attempt++)
        {
            Result allowed = await sut.CheckLockoutAsync(email, cancellationToken);
            allowed.IsSuccess.Should().BeTrue(string.Create(CultureInfo.InvariantCulture, $"attempt {attempt} is within the {settings.MaxFailedAttempts} permitted failures"));
            await sut.IncrementFailedAttemptsAsync(email, cancellationToken);
        }

        Result locked = await sut.CheckLockoutAsync(email, cancellationToken);
        locked.IsFailure.Should().BeTrue("the flow must reach the lockout so the lockout key is written and read");

        await sut.ResetFailedAttemptsAsync(email, cancellationToken);
    }

    private static async Task RunPasswordResetFlowAsync(
        ICacheService cache,
        IDistributedLock distributedLock,
        string email,
        CancellationToken cancellationToken)
    {
        var settings = new PasswordResetSettings { ResetUrl = "https://app.example.com/reset-password" };
        var sut = new PasswordResetTokenService(cache, Options.Create(settings), distributedLock, TimeProvider.System);

        Result<string> issued = await sut.IssueAsync(email, TestUserId, cancellationToken);
        issued.IsSuccess.Should().BeTrue();

        Result<UserIdentifierType> wrong = await sut.ValidateAndConsumeAsync(email, "not-the-issued-token", cancellationToken);
        wrong.IsFailure.Should().BeTrue("a wrong token is refused and counted against the record");

        Result<UserIdentifierType> redeemed = await sut.ValidateAndConsumeAsync(email, issued.Value!, cancellationToken);
        redeemed.IsSuccess.Should().BeTrue("the issued token redeems once");
    }

    private static async Task RunEmailConfirmationFlowAsync(ICacheService cache, string email, CancellationToken cancellationToken)
    {
        var settings = new EmailConfirmationSettings { ConfirmationUrl = "https://app.example.com/confirm-email" };
        var sut = new EmailConfirmationTokenService(cache, Options.Create(settings), TimeProvider.System);

        Result<string> issued = await sut.IssueAsync(email, TestUserId, cancellationToken);
        issued.IsSuccess.Should().BeTrue();

        Result<UserIdentifierType> wrong = await sut.ValidateAndConsumeAsync(email, "not-the-issued-token", cancellationToken);
        wrong.IsFailure.Should().BeTrue("a wrong token is refused and counted against the record");

        Result<UserIdentifierType> redeemed = await sut.ValidateAndConsumeAsync(email, issued.Value!, cancellationToken);
        redeemed.IsSuccess.Should().BeTrue("the issued token redeems once");
    }

    // ── Assertions ──
    private static void AssertKeysArePseudonymous(IReadOnlyCollection<string> keys, string[] ownedPrefixes)
    {
        foreach (var prefix in ownedPrefixes)
        {
            keys.Should().Contain(
                k => k.StartsWith(prefix, StringComparison.Ordinal),
                $"the flow touches every key the service owns, so at least one key starts with '{prefix}'");
        }

        keys.Should().OnlyContain(
            k => ownedPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)),
            "every key the service touches keeps one of its own prefixes");
        keys.Where(MentionsAddress).Distinct(StringComparer.Ordinal).Should().BeEmpty(
            "a cache key reaches Redis key names, log lines and exception messages, so it must never carry the address or its local part");
    }

    private static void AssertOneKeyPerPrefix(IReadOnlyCollection<string> keys, string[] ownedPrefixes)
    {
        foreach (var prefix in ownedPrefixes)
        {
            keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Should().ContainSingle(
                $"two spellings of one address must resolve to one '{prefix}' key, or an attacker gets a fresh counter per spelling");
        }
    }

    private static bool MentionsAddress(string text) =>
        text.Contains(TestEmail, StringComparison.OrdinalIgnoreCase)
        || text.Contains(LocalPart, StringComparison.OrdinalIgnoreCase);

    /// <summary>In-memory <see cref="ICacheService"/> recording every key read, written or removed.</summary>
    private sealed class RecordingCacheService : ICacheService
    {
        private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _keys = new();

        public IReadOnlyCollection<string> Keys => _keys;

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            _keys.Enqueue(key);
            return Task.FromResult(_values.TryGetValue(key, out object? value) ? (T?)value : default);
        }

        public Task<T?> GetFromSharedStoreAsync<T>(string key, CancellationToken cancellationToken = default) =>
            GetAsync<T>(key, cancellationToken);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        {
            _keys.Enqueue(key);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _keys.Enqueue(key);
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            _keys.Enqueue(prefix);
            foreach (string key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _values.TryRemove(key, out _);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The real in-process lock, recording every lock name requested.</summary>
    private sealed class RecordingDistributedLock : IDistributedLock
    {
        private readonly InProcessDistributedLock _inner = new(NullLogger<InProcessDistributedLock>.Instance);
        private readonly ConcurrentQueue<string> _keys = new();

        public IReadOnlyCollection<string> Keys => _keys;

        public Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan ttl, TimeSpan wait, CancellationToken cancellationToken = default)
        {
            _keys.Enqueue(key);
            return _inner.TryAcquireAsync(key, ttl, wait, cancellationToken);
        }
    }

    /// <summary>
    /// A degraded shared store (Redis), faulting reads in the two ways a real one does. A read of a
    /// key it does not hold times out the way StackExchange.Redis reports it, quoting the command
    /// and the key (<see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/> absorbs this one
    /// and logs it with the exception attached). A read of a flag it holds comes back with the JSON
    /// literal damaged, an entry this build can no longer deserialize, which is the fault that
    /// reaches the two-level cache's own read-failure warning. Counters, writes and removes are
    /// untouched, so the failed-login flow still reaches the lockout and the self-heal still runs.
    /// </summary>
    private sealed class ReadFaultingStore : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public byte[]? Get(string key)
        {
            if (!_store.TryGetValue(key, out var stored))
            {
                throw new TimeoutException($"Timeout performing HMGET (5000ms), next: HMGET {key}, inst: 1, qu: 0, qs: 1, aw: False, rs: ReadAsync, ws: Idle, in: 0, serverEndpoint: redis:6379");
            }

            return DamageFlagLiteral(stored);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            try
            {
                return Task.FromResult(Get(key));
            }
            catch (TimeoutException ex)
            {
                return Task.FromException<byte[]?>(ex);
            }
        }

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

        /// <summary>Returns a copy of <paramref name="stored"/> with every JSON true literal turned into the invalid literal trux.</summary>
        private static byte[] DamageFlagLiteral(byte[] stored)
        {
            var copy = (byte[])stored.Clone();
            for (var i = 0; i + 3 < copy.Length; i++)
            {
                if (copy[i] == (byte)'t' && copy[i + 1] == (byte)'r' && copy[i + 2] == (byte)'u' && copy[i + 3] == (byte)'e')
                {
                    copy[i + 3] = (byte)'x';
                }
            }

            return copy;
        }
    }

    /// <summary>One captured log call: the formatted message, every structured value, and the exception.</summary>
    private sealed record CapturedLog(string Category, LogLevel Level, string Message, IReadOnlyList<string> StateValues, Exception? Exception);

    /// <summary><see cref="ILoggerProvider"/> capturing every log call from every category.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyCollection<CapturedLog> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.Select(p => p.Key + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)).ToList()
                    : [];
                sink.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values, exception));
            }
        }
    }
}
