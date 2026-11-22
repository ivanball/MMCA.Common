using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Users;
using MMCA.Common.Application.Users.UseCases.TwoFactor;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using OtpNet;

namespace MMCA.Common.Infrastructure.Tests.Auth.TwoFactor;

/// <summary>
/// The two-factor shared secret at rest, observed end to end through the services
/// <c>AddTwoFactorAuthentication</c> registers: the value the app's <see cref="ITwoFactorStore"/>
/// receives at enrollment is not the Base32 secret the user keys an authenticator with, and a code
/// generated from that real secret still confirms the enrollment and passes the sign-in challenge.
/// </summary>
public sealed class TwoFactorSecretProtectionEndToEndTests
{
    private const UserIdentifierType AccountId = 11;

    [Fact]
    public async Task BeginEnrollment_HandsTheStoreAValueThatIsNotTheBase32Secret()
    {
        var store = new RecordingTwoFactorStore();
        await using var provider = BuildProvider(store);

        TwoFactorSetupResponse setup = await BeginAsync(provider);

        string received = store.ReceivedSecrets.Should().ContainSingle().Subject;
        setup.SharedKey.Should().NotBeNullOrWhiteSpace();
        received.Should().NotBe(setup.SharedKey, "the store must never hold the plaintext secret");
        received.Should().NotContainEquivalentOf(setup.SharedKey, "the plaintext must not be embedded in the stored form either");
    }

    [Fact]
    public async Task ConfirmEnrollment_WithACodeFromTheEnrolledSecret_EnablesTheFactor()
    {
        var store = new RecordingTwoFactorStore();
        await using var provider = BuildProvider(store);
        TwoFactorSetupResponse setup = await BeginAsync(provider);

        using var scope = provider.CreateScope();
        var confirm = new ConfirmHandler(
            scope.ServiceProvider.GetRequiredService<ITwoFactorService>(),
            scope.ServiceProvider.GetRequiredService<ITwoFactorAuthenticator>(),
            store);

        Result<TwoFactorRecoveryCodesResponse> result = await confirm.HandleAsync(
            new EnrollmentCommand(AccountId, new TwoFactorCodeRequest(CodeFor(setup.SharedKey))),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("a code generated from the secret the user enrolled with must verify");
        result.Value!.RecoveryCodes.Should().NotBeEmpty();
        store.State!.IsTwoFactorEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SignInChallenge_WithACodeFromTheEnrolledSecret_Verifies()
    {
        var store = new RecordingTwoFactorStore();
        await using var provider = BuildProvider(store);
        TwoFactorSetupResponse setup = await BeginAsync(provider);
        await store.CompleteEnrollmentAsync(AccountId, [], TestContext.Current.CancellationToken);

        using var scope = provider.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<ITwoFactorAuthenticator>();

        Result<TwoFactorOutcome> result = await authenticator.ChallengeAsync(
            AccountId, CodeFor(setup.SharedKey), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(TwoFactorOutcome.VerifiedTotp);
    }

    [Fact]
    public async Task SignInChallenge_WithACodeFromAnotherSecret_IsRefused()
    {
        var store = new RecordingTwoFactorStore();
        await using var provider = BuildProvider(store);
        await BeginAsync(provider);
        await store.CompleteEnrollmentAsync(AccountId, [], TestContext.Current.CancellationToken);
        string otherSecret = provider.GetRequiredService<ITwoFactorService>().GenerateSecret();

        using var scope = provider.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<ITwoFactorAuthenticator>();

        Result<TwoFactorOutcome> result = await authenticator.ChallengeAsync(
            AccountId, CodeFor(otherSecret), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
    }

    // -- Helpers --
    private static ServiceProvider BuildProvider(RecordingTwoFactorStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwoFactorAuthentication(new ConfigurationBuilder().Build());

        // An in-memory key ring, so the test writes no keys to the user profile; the protector
        // AddTwoFactorAuthentication registered is the one under test.
        services.Replace(ServiceDescriptor.Singleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()));
        services.AddSingleton<ITwoFactorStore>(store);
        services.AddSingleton<ICacheService, InMemoryCacheService>();
        services.AddSingleton<IDistributedLock, FreeLock>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<TwoFactorSetupResponse> BeginAsync(ServiceProvider provider)
    {
        var begin = new BeginHandler(
            provider.GetRequiredService<ITwoFactorService>(),
            provider.GetRequiredService<ITwoFactorStore>(),
            provider.GetRequiredService<ITwoFactorSecretProtector>());

        Result<TwoFactorSetupResponse> result = await begin.HandleAsync(
            new EnrollmentCommand(AccountId, new TwoFactorCodeRequest(string.Empty)),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    private static string CodeFor(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp(DateTime.UtcNow);

    /// <summary>The app-side command shape the handler bases read.</summary>
    private sealed record EnrollmentCommand(UserIdentifierType UserId, TwoFactorCodeRequest Request)
        : IUserScopedCommand<TwoFactorCodeRequest>;

    private sealed class BeginHandler(ITwoFactorService service, ITwoFactorStore store, ITwoFactorSecretProtector protector)
        : BeginTwoFactorEnrollmentHandlerBase<EnrollmentCommand>(service, store, protector, NullLogger.Instance)
    {
        protected override Task<string?> ResolveAccountNameAsync(UserIdentifierType userId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("someone@example.com");
    }

    private sealed class ConfirmHandler(ITwoFactorService service, ITwoFactorAuthenticator authenticator, ITwoFactorStore store)
        : ConfirmTwoFactorEnrollmentHandlerBase<EnrollmentCommand>(
            service, authenticator, store, new PermissiveLoginProtection(), NullLogger.Instance);

    /// <summary>A login-protection service that never locks out.</summary>
    private sealed class PermissiveLoginProtection : ILoginProtectionService
    {
        public Task<Result> CheckLockoutAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task IncrementFailedAttemptsAsync(string email, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetFailedAttemptsAsync(string email, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> CheckRegistrationRateLimitAsync(string? ipAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task IncrementRegistrationCountAsync(string? ipAddress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>An uncontended <see cref="IDistributedLock"/>: every acquire succeeds.</summary>
    private sealed class FreeLock : IDistributedLock, IAsyncDisposable
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(
            string key,
            TimeSpan ttl,
            TimeSpan wait,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(this);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>In-memory <see cref="ICacheService"/>.</summary>
    private sealed class InMemoryCacheService : ICacheService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out object? value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            foreach (string key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An <see cref="ITwoFactorStore"/> that keeps exactly what it is handed, the way an app's
    /// aggregate would, and records every secret <see cref="StartEnrollmentAsync"/> received.
    /// </summary>
    private sealed class RecordingTwoFactorStore : ITwoFactorStore
    {
        public List<string> ReceivedSecrets { get; } = [];

        public AccountState? State { get; private set; } = new(false, null, []);

        public Task<ITwoFactorUserState?> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ITwoFactorUserState?>(State);

        public Task<Result> StartEnrollmentAsync(UserIdentifierType userId, string secret, CancellationToken cancellationToken = default)
        {
            ReceivedSecrets.Add(secret);
            State = new AccountState(false, secret, []);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> CompleteEnrollmentAsync(
            UserIdentifierType userId,
            IReadOnlyCollection<string> recoveryCodeHashes,
            CancellationToken cancellationToken = default)
        {
            State = new AccountState(true, State?.TwoFactorSecret, [.. recoveryCodeHashes]);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DisableAsync(UserIdentifierType userId, CancellationToken cancellationToken = default)
        {
            State = new AccountState(false, null, []);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ReplaceRecoveryCodesAsync(
            UserIdentifierType userId,
            IReadOnlyCollection<string> recoveryCodeHashes,
            CancellationToken cancellationToken = default)
        {
            State = new AccountState(State?.IsTwoFactorEnabled ?? false, State?.TwoFactorSecret, [.. recoveryCodeHashes]);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ConsumeRecoveryCodeAsync(
            UserIdentifierType userId,
            string recoveryCodeHash,
            CancellationToken cancellationToken = default)
        {
            var remaining = State!.TwoFactorRecoveryCodeHashes.Where(h => h != recoveryCodeHash).ToList();
            State = new AccountState(State.IsTwoFactorEnabled, State.TwoFactorSecret, remaining);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed record AccountState(
        bool IsTwoFactorEnabled,
        string? TwoFactorSecret,
        IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes) : ITwoFactorUserState;
}
