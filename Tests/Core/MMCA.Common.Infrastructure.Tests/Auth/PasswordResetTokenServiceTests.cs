using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies the forgot-password token lifecycle: single use, the wrong-token attempt cap, the
/// per-email request throttle, the address normalization that keeps casing variants on one record,
/// and the hash-at-rest guarantee.
/// </summary>
public sealed class PasswordResetTokenServiceTests
{
    private const string TestEmail = "user@example.com";
    private const string TokenKey = $"pwdreset:token:{TestEmail}";
    private const string RequestKey = $"pwdreset:req:{TestEmail}";
    private const UserIdentifierType TestUserId = 42;

    // ── Issue and consume ──
    [Fact]
    public async Task IssueAsync_ThenValidateAndConsumeAsync_ResolvesTheAccountExactlyOnce()
    {
        var (sut, cache) = CreateSut();
        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);

        issued.IsSuccess.Should().BeTrue();
        Result<UserIdentifierType> first = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);
        Result<UserIdentifierType> second = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be(TestUserId);
        second.IsFailure.Should().BeTrue("a consumed token must never redeem twice");
        second.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidResetToken" && e.Type == ErrorType.Unauthorized);
        cache.Values.Should().NotContainKey(TokenKey);
        cache.Values.Should().NotContainKey(RequestKey, "a successful reset clears the request counter too");
    }

    [Fact]
    public async Task IssueAsync_Twice_LeavesOnlyTheNewestTokenRedeemable()
    {
        var (sut, _) = CreateSut();
        Result<string> older = await sut.IssueAsync(TestEmail, TestUserId);
        Result<string> newer = await sut.IssueAsync(TestEmail, TestUserId);

        Result<UserIdentifierType> withOlder = await sut.ValidateAndConsumeAsync(TestEmail, older.Value!);
        Result<UserIdentifierType> withNewer = await sut.ValidateAndConsumeAsync(TestEmail, newer.Value!);

        withOlder.IsFailure.Should().BeTrue("issuing overwrites the record, so the previous link stops working");
        withNewer.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_WithNoOutstandingToken_Fails()
    {
        var (sut, _) = CreateSut();

        Result<UserIdentifierType> result = await sut.ValidateAndConsumeAsync(TestEmail, "anything");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidResetToken");
    }

    [Fact]
    public async Task IssueAsync_WritesTheTokenLifetimeAsTheRecordTtl()
    {
        var (sut, cache) = CreateSut(tokenLifetimeMinutes: 15);

        await sut.IssueAsync(TestEmail, TestUserId);

        cache.Ttls[TokenKey].Should().Be(TimeSpan.FromMinutes(15));
    }

    // ── Attempt cap ──
    [Fact]
    public async Task ValidateAndConsumeAsync_BelowTheAttemptCap_LeavesTheRecordRedeemable()
    {
        var (sut, _) = CreateSut(maxValidationAttempts: 3);
        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);

        await sut.ValidateAndConsumeAsync(TestEmail, "wrong-1");
        await sut.ValidateAndConsumeAsync(TestEmail, "wrong-2");

        Result<UserIdentifierType> result = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_AtTheAttemptCap_WipesTheRecord()
    {
        var (sut, cache) = CreateSut(maxValidationAttempts: 3);
        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);

        for (int i = 0; i < 3; i++)
        {
            await sut.ValidateAndConsumeAsync(TestEmail, "wrong");
        }

        cache.Values.Should().NotContainKey(TokenKey);
        Result<UserIdentifierType> result = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);
        result.IsFailure.Should().BeTrue("the capped-out record is gone, so even the real token has to be re-requested");
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_WrongToken_DoesNotExtendTheRecordLifetime()
    {
        var (sut, cache) = CreateSut(tokenLifetimeMinutes: 30, maxValidationAttempts: 5);
        await sut.IssueAsync(TestEmail, TestUserId);

        await sut.ValidateAndConsumeAsync(TestEmail, "wrong");

        cache.Ttls[TokenKey]!.Value.Should().BeLessThanOrEqualTo(
            TimeSpan.FromMinutes(30),
            "a wrong guess must not buy the token a fresh lifetime");
    }

    // ── Per-email request throttle ──
    [Fact]
    public async Task IssueAsync_BeyondTheRequestLimit_FailsAsThrottled()
    {
        var (sut, _) = CreateSut(maxRequestsPerEmail: 3);

        for (int i = 0; i < 3; i++)
        {
            (await sut.IssueAsync(TestEmail, TestUserId)).IsSuccess.Should().BeTrue();
        }

        Result<string> throttled = await sut.IssueAsync(TestEmail, TestUserId);

        throttled.IsFailure.Should().BeTrue();
        throttled.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.ResetThrottled" && e.Type == ErrorType.Unauthorized);
    }

    [Fact]
    public async Task IssueAsync_WritesTheRequestWindowAsTheCounterTtl()
    {
        var (sut, cache) = CreateSut(requestWindowMinutes: 45);

        await sut.IssueAsync(TestEmail, TestUserId);

        cache.Ttls[RequestKey].Should().Be(TimeSpan.FromMinutes(45));
    }

    // ── Address normalization ──
    [Theory]
    [InlineData("User@Example.com")]
    [InlineData("USER@EXAMPLE.COM")]
    [InlineData("  user@example.com  ")]
    public async Task IssueAsync_EmailVariants_ShareOneRecordAndOneCounter(string variant)
    {
        var (sut, cache) = CreateSut();

        Result<string> issued = await sut.IssueAsync(variant, TestUserId);

        cache.Values.Should().HaveCount(2, "one token record and one request counter, both on the normalized key");
        cache.Values.Should().ContainKey(TokenKey);
        cache.Values.Should().ContainKey(RequestKey);
        (await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!)).IsSuccess.Should().BeTrue(
            "a token issued for a variant redeems against the normalized address");
    }

    // ── Hash at rest ──
    [Fact]
    public async Task IssueAsync_StoresOnlyTheTokenHash()
    {
        var (sut, cache) = CreateSut();

        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);

        var entry = (PasswordResetEntry)cache.Values[TokenKey]!;
        entry.TokenHashBase64.Should().NotBe(issued.Value);
        entry.TokenHashBase64.Should().Be(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Value!))),
            "a cache dump must not hand out working reset links");
        entry.UserId.Should().Be(TestUserId);
        entry.FailedAttempts.Should().Be(0);
    }

    [Fact]
    public async Task CachedEntry_RoundTripsThroughJson()
    {
        // The distributed cache serializes with System.Text.Json, so a record that cannot round-trip
        // would work against an in-memory cache and fail against Redis.
        var (sut, cache) = CreateSut();
        await sut.IssueAsync(TestEmail, TestUserId);
        var entry = (PasswordResetEntry)cache.Values[TokenKey]!;

        var roundTripped = JsonSerializer.Deserialize<PasswordResetEntry>(JsonSerializer.Serialize(entry));

        roundTripped.Should().Be(entry);
    }

    // ── Helpers ──
    private static (PasswordResetTokenService Sut, FakeCacheService Cache) CreateSut(
        int tokenLifetimeMinutes = 30,
        int maxValidationAttempts = 5,
        int maxRequestsPerEmail = 100,
        int requestWindowMinutes = 60)
    {
        var cache = new FakeCacheService();
        var settings = new PasswordResetSettings
        {
            ResetUrl = "https://app.example.com/reset-password",
            TokenLifetimeMinutes = tokenLifetimeMinutes,
            MaxValidationAttempts = maxValidationAttempts,
            MaxRequestsPerEmail = maxRequestsPerEmail,
            RequestWindowMinutes = requestWindowMinutes,
        };

        return (new PasswordResetTokenService(cache, Options.Create(settings)), cache);
    }

    /// <summary>In-memory <see cref="ICacheService"/> recording every value and TTL written.</summary>
    private sealed class FakeCacheService : ICacheService
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TimeSpan?> Ttls { get; } = new(StringComparer.Ordinal);

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
