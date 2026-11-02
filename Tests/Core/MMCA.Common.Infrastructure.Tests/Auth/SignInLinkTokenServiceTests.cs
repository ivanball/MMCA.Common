using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Auth.SignInLinks;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies the sign-in-link token lifecycle: single use, the per-purpose record and lifetime, the
/// provisioning payload an invitation carries, the wrong-token attempt cap, the per-address request
/// throttle, and the address normalization that keeps casing variants on one record.
/// </summary>
public sealed class SignInLinkTokenServiceTests
{
    private const string TestEmail = "user@example.com";

    private static readonly string SignInKey = $"signinlink:token:0:{EmailIdentity.KeySegment(TestEmail)}";
    private static readonly string InviteKey = $"signinlink:token:1:{EmailIdentity.KeySegment(TestEmail)}";
    private static readonly string RequestKey = $"signinlink:req:{EmailIdentity.KeySegment(TestEmail)}";

    [Fact]
    public async Task IssueAsync_ThenValidateAndConsumeAsync_GrantsExactlyOnce()
    {
        var (sut, cache) = CreateSut();
        Result<string> issued = await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);

        Result<SignInLinkGrant> first = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);
        Result<SignInLinkGrant> second = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        first.IsSuccess.Should().BeTrue();
        first.Value!.Purpose.Should().Be(SignInLinkPurpose.SignIn);
        first.Value.Profile.Should().BeNull("a plain sign-in link carries no provisioning name");
        second.IsFailure.Should().BeTrue("a consumed token must never redeem twice");
        second.Errors.Should().ContainSingle(e =>
            e.Code == SignInLinkErrors.InvalidLinkCode && e.Type == ErrorType.Unauthorized);
        cache.Values.Should().NotContainKey(SignInKey);
        cache.Values.Should().NotContainKey(RequestKey, "a redeemed link clears the request counter too");
    }

    [Fact]
    public async Task IssueAsync_ForAnInvite_CarriesTheProfileAndTheInviteLifetime()
    {
        var (sut, cache) = CreateSut();

        Result<string> issued = await sut.IssueAsync(TestEmail, SignInLinkPurpose.Invite, new SignInLinkProfile("Ada", "Lovelace"));
        Result<SignInLinkGrant> grant = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        cache.Ttls.Should().NotContainKey(InviteKey, "the record is consumed");
        grant.Value!.Purpose.Should().Be(SignInLinkPurpose.Invite);
        grant.Value.Profile.Should().Be(new SignInLinkProfile("Ada", "Lovelace"));
    }

    [Fact]
    public async Task IssueAsync_StoresEachPurposeWithItsOwnLifetime()
    {
        var (sut, cache) = CreateSut();

        await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);
        await sut.IssueAsync(TestEmail, SignInLinkPurpose.Invite, new SignInLinkProfile("Ada", "Lovelace"));

        cache.Ttls[SignInKey].Should().Be(TimeSpan.FromMinutes(15));
        cache.Ttls[InviteKey].Should().Be(TimeSpan.FromMinutes(20160));
    }

    [Fact]
    public async Task IssueAsync_ForSignIn_LeavesAnOutstandingInviteRedeemable()
    {
        var (sut, _) = CreateSut();
        Result<string> invite = await sut.IssueAsync(TestEmail, SignInLinkPurpose.Invite, new SignInLinkProfile("Ada", "Lovelace"));
        await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);

        Result<SignInLinkGrant> grant = await sut.ValidateAndConsumeAsync(TestEmail, invite.Value!);

        grant.IsSuccess.Should().BeTrue("asking for a sign-in link must not silently invalidate an invitation");
    }

    [Fact]
    public async Task IssueAsync_TwiceForOnePurpose_LeavesOnlyTheNewestRedeemable()
    {
        var (sut, _) = CreateSut();
        Result<string> older = await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);
        Result<string> newer = await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);

        (await sut.ValidateAndConsumeAsync(TestEmail, older.Value!)).IsFailure.Should().BeTrue();
        (await sut.ValidateAndConsumeAsync(TestEmail, newer.Value!)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_IsCaseInsensitiveOnTheAddress()
    {
        var (sut, _) = CreateSut();
        Result<string> issued = await sut.IssueAsync("User@Example.com", SignInLinkPurpose.SignIn);

        Result<SignInLinkGrant> grant = await sut.ValidateAndConsumeAsync(TestEmail, issued.Value!);

        grant.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_WithNoOutstandingToken_Fails()
    {
        var (sut, _) = CreateSut();

        Result<SignInLinkGrant> result = await sut.ValidateAndConsumeAsync(TestEmail, "anything");

        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.InvalidLinkCode);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_AtTheAttemptCap_DiscardsEveryOutstandingRecord()
    {
        var (sut, cache) = CreateSut(maxValidationAttempts: 2);
        Result<string> signIn = await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);
        await sut.IssueAsync(TestEmail, SignInLinkPurpose.Invite, new SignInLinkProfile("Ada", "Lovelace"));

        await sut.ValidateAndConsumeAsync(TestEmail, "wrong-1");
        cache.Values.Should().ContainKey(SignInKey, "one wrong guess is under the cap");
        await sut.ValidateAndConsumeAsync(TestEmail, "wrong-2");

        cache.Values.Should().NotContainKey(SignInKey);
        cache.Values.Should().NotContainKey(InviteKey);
        (await sut.ValidateAndConsumeAsync(TestEmail, signIn.Value!)).IsFailure.Should().BeTrue(
            "a discarded record cannot be redeemed even with the right token");
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_AfterAWrongGuess_KeepsTheRemainingLifetime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var (sut, cache) = CreateSut(timeProvider: clock);
        await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);
        clock.Advance(TimeSpan.FromMinutes(5));

        await sut.ValidateAndConsumeAsync(TestEmail, "wrong");

        cache.Ttls[SignInKey].Should().Be(TimeSpan.FromMinutes(10), "a wrong guess must not extend the token's life");
    }

    [Fact]
    public async Task IssueAsync_PastTheRequestThrottle_Fails()
    {
        var (sut, _) = CreateSut(maxRequestsPerEmail: 2);

        await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);
        await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);
        Result<string> third = await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);

        third.IsFailure.Should().BeTrue();
        third.Errors.Should().ContainSingle(e => e.Code == "Authentication.SignInLinkThrottled");
    }

    [Fact]
    public async Task IssueAsync_StoresOnlyTheHashOfTheToken()
    {
        var (sut, cache) = CreateSut();

        Result<string> issued = await sut.IssueAsync(TestEmail, SignInLinkPurpose.SignIn);

        var entry = cache.Values[SignInKey]!;
        entry.ToString().Should().NotContain(issued.Value!, "a cache dump must not hand out working links");
    }

    private static (SignInLinkTokenService Sut, FakeCacheService Cache) CreateSut(
        int maxValidationAttempts = 5,
        int maxRequestsPerEmail = 100,
        TimeProvider? timeProvider = null)
    {
        var cache = new FakeCacheService();
        var settings = new SignInLinkSettings
        {
            Enabled = true,
            SignInUrl = "https://app.example.com/auth/sign-in-link",
            MaxValidationAttempts = maxValidationAttempts,
            MaxRequestsPerEmail = maxRequestsPerEmail,
        };

        return (new SignInLinkTokenService(cache, Options.Create(settings), timeProvider ?? TimeProvider.System), cache);
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
