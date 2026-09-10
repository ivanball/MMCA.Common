using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;
using OtpNet;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies the sign-in second-factor challenge: an account with no active factor passes straight
/// through, an enrolled account with no code is told to supply one, a live code verifies, and a
/// recovery code verifies exactly once because the challenge spends it.
/// </summary>
public sealed class TwoFactorAuthenticatorTests
{
    private const UserIdentifierType TestUserId = 7;

    [Fact]
    public async Task ChallengeAsync_ForAnAccountWithNoSecondFactor_IsNotEnrolled()
    {
        var (sut, store, _) = CreateSut(enabled: false);

        Result<TwoFactorOutcome> result = await sut.ChallengeAsync(TestUserId, code: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(TwoFactorOutcome.NotEnrolled);
        store.ConsumedHashes.Should().BeEmpty();
    }

    [Fact]
    public async Task ChallengeAsync_ForAnUnknownAccount_IsNotEnrolled()
    {
        // The caller has already decided the account exists; a row that vanished in between is not
        // this method's place to turn into a different error.
        var (sut, _, _) = CreateSut(enabled: true, stateExists: false);

        Result<TwoFactorOutcome> result = await sut.ChallengeAsync(TestUserId, code: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(TwoFactorOutcome.NotEnrolled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChallengeAsync_WithNoCodeOnAnEnrolledAccount_AsksForOne(string? code)
    {
        var (sut, _, _) = CreateSut(enabled: true);

        Result<TwoFactorOutcome> result = await sut.ChallengeAsync(TestUserId, code);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == TwoFactorErrors.TwoFactorRequiredCode && e.Type == ErrorType.Unauthorized);
    }

    [Fact]
    public async Task ChallengeAsync_WithTheLiveTimeBasedCode_VerifiesWithoutSpendingARecoveryCode()
    {
        var (sut, store, secret) = CreateSut(enabled: true);

        Result<TwoFactorOutcome> result = await sut.ChallengeAsync(TestUserId, CodeAt(secret));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(TwoFactorOutcome.VerifiedTotp);
        store.ConsumedHashes.Should().BeEmpty();
    }

    [Fact]
    public async Task ChallengeAsync_WithAWrongCode_Fails()
    {
        var (sut, store, _) = CreateSut(enabled: true);

        Result<TwoFactorOutcome> result = await sut.ChallengeAsync(TestUserId, "000000");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
        store.ConsumedHashes.Should().BeEmpty();
    }

    [Fact]
    public async Task ChallengeAsync_WithARecoveryCode_VerifiesAndSpendsIt()
    {
        var (sut, store, _) = CreateSut(enabled: true, recoveryCodeCount: 3);
        string code = store.PlaintextRecoveryCodes[1];

        Result<TwoFactorOutcome> first = await sut.ChallengeAsync(TestUserId, code);
        Result<TwoFactorOutcome> second = await sut.ChallengeAsync(TestUserId, code);

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be(TwoFactorOutcome.VerifiedRecoveryCode);
        store.ConsumedHashes.Should().ContainSingle();
        second.IsFailure.Should().BeTrue("a recovery code is single use");
        second.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
    }

    [Fact]
    public async Task ChallengeAsync_WhenSpendingTheRecoveryCodeFails_DoesNotLetTheSignInThrough()
    {
        // Letting it through would hand out a sign-in on a code that is still live.
        var (sut, store, _) = CreateSut(enabled: true, recoveryCodeCount: 2);
        store.ConsumeFailure = Error.Failure("Test.SaveFailed", "The store could not persist the redemption.");

        Result<TwoFactorOutcome> result = await sut.ChallengeAsync(TestUserId, store.PlaintextRecoveryCodes[0]);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Test.SaveFailed");
    }

    // ── Helpers ──
    private static (TwoFactorAuthenticator Sut, FakeTwoFactorStore Store, string Secret) CreateSut(
        bool enabled,
        bool stateExists = true,
        int recoveryCodeCount = 0)
    {
        var settings = Options.Create(new TwoFactorSettings { RecoveryCodeCount = Math.Max(recoveryCodeCount, 1) });
        var service = new TotpTwoFactorService(settings);
        string secret = service.GenerateSecret();

        var store = new FakeTwoFactorStore();
        if (stateExists)
        {
            RecoveryCodeSet? set = recoveryCodeCount > 0 ? service.GenerateRecoveryCodes() : null;
            store.Seed(enabled, secret, set);
        }

        return (new TwoFactorAuthenticator(service, store), store, secret);
    }

    private static string CodeAt(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

    /// <summary>In-memory <see cref="ITwoFactorStore"/> recording which recovery hashes were spent.</summary>
    private sealed class FakeTwoFactorStore : ITwoFactorStore
    {
        private State? _state;

        public List<string> PlaintextRecoveryCodes { get; } = [];

        public List<string> ConsumedHashes { get; } = [];

        public Error? ConsumeFailure { get; set; }

        public void Seed(bool enabled, string secret, RecoveryCodeSet? recoveryCodes)
        {
            _state = new State(enabled, secret, [.. recoveryCodes?.Hashes ?? []]);
            PlaintextRecoveryCodes.AddRange(recoveryCodes?.Codes ?? []);
        }

        public Task<ITwoFactorUserState?> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ITwoFactorUserState?>(_state);

        public Task<Result> StartEnrollmentAsync(UserIdentifierType userId, string secret, CancellationToken cancellationToken = default)
        {
            _state = new State(false, secret, []);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> CompleteEnrollmentAsync(
            UserIdentifierType userId,
            IReadOnlyCollection<string> recoveryCodeHashes,
            CancellationToken cancellationToken = default)
        {
            _state = new State(true, _state?.TwoFactorSecret, [.. recoveryCodeHashes]);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DisableAsync(UserIdentifierType userId, CancellationToken cancellationToken = default)
        {
            _state = new State(false, null, []);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ReplaceRecoveryCodesAsync(
            UserIdentifierType userId,
            IReadOnlyCollection<string> recoveryCodeHashes,
            CancellationToken cancellationToken = default)
        {
            _state = new State(_state?.IsTwoFactorEnabled ?? false, _state?.TwoFactorSecret, [.. recoveryCodeHashes]);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ConsumeRecoveryCodeAsync(
            UserIdentifierType userId,
            string recoveryCodeHash,
            CancellationToken cancellationToken = default)
        {
            if (ConsumeFailure is { } error)
            {
                return Task.FromResult(Result.Failure(error));
            }

            ConsumedHashes.Add(recoveryCodeHash);
            var remaining = _state!.TwoFactorRecoveryCodeHashes.Where(h => h != recoveryCodeHash).ToList();
            _state = new State(_state.IsTwoFactorEnabled, _state.TwoFactorSecret, remaining);

            return Task.FromResult(Result.Success());
        }

        private sealed record State(
            bool IsTwoFactorEnabled,
            string? TwoFactorSecret,
            IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes) : ITwoFactorUserState;
    }
}
