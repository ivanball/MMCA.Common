using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies the email-confirmation token lifecycle: single use, the wrong-token attempt cap, the
/// per-address request throttle, the address normalization that keeps casing variants on one record,
/// the hash-at-rest guarantee, and the key separation that keeps a confirmation link from clobbering
/// an outstanding password-reset link.
/// </summary>
public sealed class EmailConfirmationTokenServiceTests
{
    private const string TestEmail = "user@example.com";
    private const string TokenKey = $"emailconfirm:token:{TestEmail}";
    private const string RequestKey = $"emailconfirm:req:{TestEmail}";
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
            e.Code == EmailConfirmationErrors.InvalidTokenCode && e.Type == ErrorType.Unauthorized);
        cache.Values.Should().NotContainKey(TokenKey);
        cache.Values.Should().NotContainKey(RequestKey, "a successful confirmation clears the request counter too");
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
        result.Errors.Should().ContainSingle(e => e.Code == EmailConfirmationErrors.InvalidTokenCode);
    }

    // ── Expiry ──
    [Fact]
    public async Task IssueAsync_StoresTheRecordWithTheConfiguredLifetimeAsItsTtl()
    {
        var (sut, cache) = CreateSut(tokenLifetimeMinutes: 1440);

        await sut.IssueAsync(TestEmail, TestUserId);

        cache.Ttls[TokenKey].Should().Be(TimeSpan.FromMinutes(1440));
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_AfterTheRecordExpired_Fails()
    {
        // Expiry is enforced by the cache TTL, so an expired token is modelled the way the cache
        // presents it: the record is simply gone.
        var (sut, cache) = CreateSut();
        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);
        cache.Values.Remove(TokenKey);

        Result<UserIdentifierType> result = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == EmailConfirmationErrors.InvalidTokenCode);
    }

    // ── Attempt cap and throttle ──
    [Fact]
    public async Task ValidateAndConsumeAsync_AfterTheAttemptCap_DiscardsTheRecord()
    {
        var (sut, cache) = CreateSut(maxValidationAttempts: 3);
        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            (await sut.ValidateAndConsumeAsync(TestEmail, "wrong")).IsFailure.Should().BeTrue();
        }

        cache.Values.Should().NotContainKey(TokenKey);
        (await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!)).IsFailure.Should()
            .BeTrue("the record is gone, so even the right token no longer redeems");
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_WithAWrongToken_DoesNotExtendTheRemainingLifetime()
    {
        var (sut, cache) = CreateSut(tokenLifetimeMinutes: 60, maxValidationAttempts: 5);
        await sut.IssueAsync(TestEmail, TestUserId);

        await sut.ValidateAndConsumeAsync(TestEmail, "wrong");

        // The rewrite carries the REMAINING lifetime. Measured in whole seconds it can land exactly on
        // the original value when no second has elapsed yet, so the invariant is that it never grows.
        cache.Ttls[TokenKey].Should().BeLessThanOrEqualTo(
            TimeSpan.FromMinutes(60),
            "a wrong guess must not be able to extend how long the token stays redeemable");
    }

    [Fact]
    public async Task IssueAsync_PastTheRequestThrottle_Fails()
    {
        var (sut, _) = CreateSut(maxRequestsPerEmail: 2);

        (await sut.IssueAsync(TestEmail, TestUserId)).IsSuccess.Should().BeTrue();
        (await sut.IssueAsync(TestEmail, TestUserId)).IsSuccess.Should().BeTrue();
        Result<string> third = await sut.IssueAsync(TestEmail, TestUserId);

        third.IsFailure.Should().BeTrue();
        third.Errors.Should().ContainSingle(e => e.Code == "Authentication.ConfirmationThrottled");
    }

    // ── Normalization, hashing and key separation ──
    [Fact]
    public async Task ValidateAndConsumeAsync_MatchesACasingVariantOfTheSameAddress()
    {
        var (sut, _) = CreateSut();
        Result<string> issued = await sut.IssueAsync("User@Example.COM", TestUserId);

        Result<UserIdentifierType> result = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        result.IsSuccess.Should().BeTrue("one address must not have two independent records");
    }

    [Fact]
    public async Task IssueAsync_StoresOnlyTheHashOfTheToken()
    {
        var (sut, cache) = CreateSut();
        Result<string> issued = await sut.IssueAsync(TestEmail, TestUserId);
        var entry = (EmailConfirmationEntry)cache.Values[TokenKey]!;

        entry.TokenHashBase64.Should().NotBe(issued.Value);
        entry.TokenHashBase64.Should().Be(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Value!))),
            "a cache dump must not hand out working confirmation links");
    }

    [Fact]
    public async Task IssueAsync_UsesKeysThatCannotCollideWithThePasswordResetRecord()
    {
        // Sharing a key would make sending a confirmation link silently invalidate an outstanding
        // password-reset link for the same address.
        var (sut, cache) = CreateSut();

        await sut.IssueAsync(TestEmail, TestUserId);

        cache.Values.Keys.Should().AllSatisfy(key => key.Should().StartWith("emailconfirm:"));
    }

    [Fact]
    public async Task CachedEntry_RoundTripsThroughSystemTextJson()
    {
        // The distributed cache serializes with System.Text.Json, so a record that cannot round-trip
        // would work against an in-memory cache and fail against Redis.
        var (sut, cache) = CreateSut();
        await sut.IssueAsync(TestEmail, TestUserId);
        var entry = (EmailConfirmationEntry)cache.Values[TokenKey]!;

        var roundTripped = JsonSerializer.Deserialize<EmailConfirmationEntry>(JsonSerializer.Serialize(entry));

        roundTripped.Should().Be(entry);
    }

    // ── Helpers ──
    private static (EmailConfirmationTokenService Sut, FakeConfirmationCacheService Cache) CreateSut(
        int tokenLifetimeMinutes = 1440,
        int maxValidationAttempts = 5,
        int maxRequestsPerEmail = 100,
        int requestWindowMinutes = 60)
    {
        var cache = new FakeConfirmationCacheService();
        var settings = new EmailConfirmationSettings
        {
            ConfirmationUrl = "https://app.example.com/confirm-email",
            TokenLifetimeMinutes = tokenLifetimeMinutes,
            MaxValidationAttempts = maxValidationAttempts,
            MaxRequestsPerEmail = maxRequestsPerEmail,
            RequestWindowMinutes = requestWindowMinutes,
        };

        return (new EmailConfirmationTokenService(cache, Options.Create(settings)), cache);
    }

    /// <summary>In-memory <see cref="ICacheService"/> recording every value and TTL written.</summary>
    private sealed class FakeConfirmationCacheService : ICacheService
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
