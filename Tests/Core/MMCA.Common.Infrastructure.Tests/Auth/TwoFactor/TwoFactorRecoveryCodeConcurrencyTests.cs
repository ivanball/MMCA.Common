using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.TwoFactor;

/// <summary>
/// Pins security review 2026-11-21 item A5-04: a recovery code is matched and spent under the same
/// per-account lock the time-based code path takes, so two concurrent challenges presenting one
/// recovery code yield exactly one success. The store here deliberately leaves a gap between the read
/// and the spend (as a real database round trip does) and does not itself refuse a hash it already
/// removed, so only the lock can keep the second redemption out.
/// </summary>
public sealed class TwoFactorRecoveryCodeConcurrencyTests
{
    private const UserIdentifierType TestUserId = 11;

    [Fact]
    public async Task ChallengeAsync_TwoConcurrentRedemptionsOfOneRecoveryCode_YieldExactlyOneSuccess()
    {
        var (sut, store) = CreateSut();
        var code = store.PlaintextRecoveryCodes[0];

        Result<TwoFactorOutcome>[] results = await Task.WhenAll(
            Task.Run(() => sut.ChallengeAsync(TestUserId, code), TestContext.Current.CancellationToken),
            Task.Run(() => sut.ChallengeAsync(TestUserId, code), TestContext.Current.CancellationToken));

        results.Count(r => r.IsSuccess).Should().Be(1, "one recovery code is one sign-in, however the requests race");
        results.Single(r => r.IsSuccess).Value.Should().Be(TwoFactorOutcome.VerifiedRecoveryCode);
        results.Single(r => r.IsFailure).Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
        store.ConsumedHashes.Should().ContainSingle("the code is spent once");
    }

    [Fact]
    public async Task ChallengeAsync_ManyConcurrentRedemptionsOfOneRecoveryCode_YieldExactlyOneSuccess()
    {
        var (sut, store) = CreateSut();
        var code = store.PlaintextRecoveryCodes[0];

        Result<TwoFactorOutcome>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => sut.ChallengeAsync(TestUserId, code), TestContext.Current.CancellationToken)));

        results.Count(r => r.IsSuccess).Should().Be(1);
        store.ConsumedHashes.Should().ContainSingle();
    }

    [Fact]
    public async Task ChallengeAsync_TwoConcurrentRedemptionsOfDifferentRecoveryCodes_BothSucceed()
    {
        // The lock serializes, it does not refuse: two different live codes both redeem.
        var (sut, store) = CreateSut();

        Result<TwoFactorOutcome>[] results = await Task.WhenAll(
            Task.Run(() => sut.ChallengeAsync(TestUserId, store.PlaintextRecoveryCodes[0]), TestContext.Current.CancellationToken),
            Task.Run(() => sut.ChallengeAsync(TestUserId, store.PlaintextRecoveryCodes[1]), TestContext.Current.CancellationToken));

        results.Should().OnlyContain(r => r.IsSuccess);
        store.ConsumedHashes.Should().HaveCount(2);
    }

    // -- Helpers --
    private static (TwoFactorAuthenticator Sut, SlowTwoFactorStore Store) CreateSut()
    {
        var settings = Options.Create(new TwoFactorSettings { RecoveryCodeCount = 3 });
        var service = new TotpTwoFactorService(settings);
        var store = new SlowTwoFactorStore(service.GenerateSecret(), service.GenerateRecoveryCodes());
        var cache = new Mock<ICacheService>();
        cache
            .Setup(c => c.GetFromSharedStoreAsync<long?>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        return (new TwoFactorAuthenticator(service, store, cache.Object, settings, new InProcessKeyedLock()), store);
    }

    /// <summary>A working in-process <see cref="IDistributedLock"/>: one holder per key, waiters queue up to the wait.</summary>
    private sealed class InProcessKeyedLock : IDistributedLock
    {
        private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        public async Task<IAsyncDisposable?> TryAcquireAsync(
            string key,
            TimeSpan ttl,
            TimeSpan wait,
            CancellationToken cancellationToken = default)
        {
            SemaphoreSlim gate;
            lock (_sync)
            {
                if (!_gates.TryGetValue(key, out gate!))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _gates[key] = gate;
                }
            }

            return await gate.WaitAsync(wait, cancellationToken) ? new Release(gate) : null;
        }

        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// An <see cref="ITwoFactorStore"/> whose reads hand out a snapshot and whose spend lands only after
    /// an await, like a database round trip, and which accepts a spend of a hash it already removed.
    /// </summary>
    private sealed class SlowTwoFactorStore : ITwoFactorStore
    {
        private readonly Lock _sync = new();
        private State _state;

        public SlowTwoFactorStore(string secret, RecoveryCodeSet recoveryCodes)
        {
            _state = new State(true, secret, [.. recoveryCodes.Hashes]);
            PlaintextRecoveryCodes = [.. recoveryCodes.Codes];
        }

        public IReadOnlyList<string> PlaintextRecoveryCodes { get; }

        public List<string> ConsumedHashes { get; } = [];

        public async Task<ITwoFactorUserState?> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            lock (_sync)
            {
                return _state;
            }
        }

        public async Task<Result> ConsumeRecoveryCodeAsync(
            UserIdentifierType userId,
            string recoveryCodeHash,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            lock (_sync)
            {
                ConsumedHashes.Add(recoveryCodeHash);
                _state = _state with
                {
                    TwoFactorRecoveryCodeHashes = [.. _state.TwoFactorRecoveryCodeHashes.Where(h => h != recoveryCodeHash)],
                };
            }

            return Result.Success();
        }

        public Task<Result> StartEnrollmentAsync(UserIdentifierType userId, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> CompleteEnrollmentAsync(
            UserIdentifierType userId,
            IReadOnlyCollection<string> recoveryCodeHashes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DisableAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> ReplaceRecoveryCodesAsync(
            UserIdentifierType userId,
            IReadOnlyCollection<string> recoveryCodeHashes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private sealed record State(
            bool IsTwoFactorEnabled,
            string? TwoFactorSecret,
            IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes) : ITwoFactorUserState;
    }
}
