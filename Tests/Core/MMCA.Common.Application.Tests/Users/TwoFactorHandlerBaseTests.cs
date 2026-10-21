using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Users;
using MMCA.Common.Application.Users.UseCases.TwoFactor;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the four shared two-factor use-case bases through concrete test subclasses: enrollment
/// is two-step (a secret that is inert until a code from it verifies), disabling and regenerating
/// both demand a live code, and a regenerate replaces the whole recovery set.
/// </summary>
/// <remarks>
/// The cryptography is stubbed rather than real, deliberately: these tests are about the workflow's
/// ordering and its refusals, and the RFC 6238 behaviour is pinned where it lives, against
/// <c>TotpTwoFactorService</c> in the Infrastructure tier. Application tests may not reach into
/// Infrastructure at all (the layer rule), so the stub is also what keeps this file legal.
/// </remarks>
public sealed class TwoFactorHandlerBaseTests
{
    private const UserIdentifierType TestUserId = 11;
    private const string AttemptKey = "two-factor:11";

    // ── Begin enrollment ──
    [Fact]
    public async Task BeginEnrollment_StoresTheSecretWithoutEnablingTheFactor()
    {
        var (store, service) = CreateCollaborators();
        var sut = new TestBeginHandler(service, store, "user@example.com");

        Result<TwoFactorSetupResponse> result = await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, default));

        result.IsSuccess.Should().BeTrue();
        result.Value.SharedKey.Should().NotBeNullOrWhiteSpace();
        result.Value.ProvisioningUri.Should().Contain("otpauth://totp/");
        store.State!.TwoFactorSecret.Should().Be(result.Value.SharedKey);
        store.State.IsTwoFactorEnabled.Should().BeFalse("an abandoned enrollment must never gate a later sign-in");
    }

    [Fact]
    public async Task BeginEnrollment_ForAnAlreadyEnrolledAccount_IsRefused()
    {
        // Re-keying a live secret would break the authenticator the user signs in with today.
        var (store, service) = CreateCollaborators();
        store.SeedEnabled(service.GenerateSecret(), []);
        var sut = new TestBeginHandler(service, store, "user@example.com");

        Result<TwoFactorSetupResponse> result = await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, default));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Authentication.TwoFactorAlreadyEnabled");
    }

    [Fact]
    public async Task BeginEnrollment_WhenTheAccountCannotBeResolved_ReturnsNotFound()
    {
        var (store, service) = CreateCollaborators();
        store.StateExists = false;
        var sut = new TestBeginHandler(service, store, "user@example.com");

        Result<TwoFactorSetupResponse> result = await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, default));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
    }

    [Fact]
    public async Task BeginEnrollment_WhenTheAccountHasNoLabel_ReturnsNotFound()
    {
        var (store, service) = CreateCollaborators();
        var sut = new TestBeginHandler(service, store, accountName: null);

        Result<TwoFactorSetupResponse> result = await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, default));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
    }

    // ── Confirm enrollment ──
    [Fact]
    public async Task ConfirmEnrollment_WithACodeFromThePendingSecret_EnablesTheFactorAndReturnsRecoveryCodes()
    {
        var (store, service) = CreateCollaborators(recoveryCodeCount: 6);
        string secret = service.GenerateSecret();
        store.SeedPending(secret);
        var sut = new TestConfirmHandler(service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecoveryCodes.Should().HaveCount(6);
        store.State!.IsTwoFactorEnabled.Should().BeTrue();
        store.State.TwoFactorRecoveryCodeHashes.Should().HaveCount(6);
        store.State.TwoFactorRecoveryCodeHashes.Should().NotIntersectWith(
            result.Value.RecoveryCodes,
            "the store keeps hashes, never the codes themselves");
    }

    [Fact]
    public async Task ConfirmEnrollment_WithNoPendingSecret_IsRefused()
    {
        var (store, service) = CreateCollaborators();
        var sut = new TestConfirmHandler(service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("123456")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorEnrollmentMissingCode);
    }

    [Fact]
    public async Task ConfirmEnrollment_WithAWrongCode_LeavesTheFactorOff()
    {
        var (store, service) = CreateCollaborators();
        store.SeedPending(service.GenerateSecret());
        var sut = new TestConfirmHandler(service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("000000")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
        store.State!.IsTwoFactorEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmEnrollment_WithARecoveryCode_IsRefused()
    {
        // The step exists to prove the authenticator app really holds the secret, so only a
        // time-based code can satisfy it.
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        RecoveryCodeSet codes = service.GenerateRecoveryCodes();
        store.SeedPending(secret);
        var sut = new TestConfirmHandler(service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(codes.Codes[0])));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
    }

    [Fact]
    public async Task ConfirmEnrollment_OnAnAlreadyEnabledAccount_IsRefusedAndKeepsTheRecoveryCodes()
    {
        // M228: the secret stays stored after enrollment, so without this check a TOTP-only code
        // re-ran CompleteEnrollmentAsync and minted a fresh recovery set for a stolen-token holder.
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        RecoveryCodeSet original = service.GenerateRecoveryCodes();
        store.SeedEnabled(secret, original.Hashes);
        var sut = new TestConfirmHandler(service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Authentication.TwoFactorAlreadyEnabled");
        store.State!.TwoFactorRecoveryCodeHashes.Should().BeEquivalentTo(original.Hashes);
    }

    [Fact]
    public async Task ConfirmEnrollment_WithAWrongCode_CountsAFailedAttempt()
    {
        // M228: every post-sign-in code check feeds the per-account lockout, as change-password does.
        var (store, service) = CreateCollaborators();
        store.SeedPending(service.GenerateSecret());
        var protection = new RecordingLoginProtection();
        var sut = new TestConfirmHandler(service, store, protection);

        await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("000000")));

        protection.Increments.Should().Equal(AttemptKey);
        protection.Resets.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmEnrollment_WhenLockedOut_IsRefusedWithoutEnablingTheFactor()
    {
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedPending(secret);
        var sut = new TestConfirmHandler(service, store, new RecordingLoginProtection { LockedOut = true });

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.TooManyRequests);
        store.State!.IsTwoFactorEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmEnrollment_WithTheRightCode_ResetsTheFailedAttemptCounter()
    {
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedPending(secret);
        var protection = new RecordingLoginProtection();
        var sut = new TestConfirmHandler(service, store, protection);

        await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        protection.Resets.Should().Equal(AttemptKey);
    }

    [Fact]
    public async Task ConfirmEnrollment_TheConfirmingCodeCannotBeReplayedToDisableTheFactor()
    {
        // M228: confirm goes through the authenticator's single-use time-step guard, so the code it
        // accepted is already spent at the first challenge.
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedPending(secret);
        var authenticator = new FakeAuthenticator(service, store);
        var confirm = new TestConfirmHandler(service, store, authenticator: authenticator);
        var disable = new TestDisableHandler(authenticator, store);
        var code = new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret));

        Result<TwoFactorRecoveryCodesResponse> confirmed = await confirm.HandleAsync(new TestTwoFactorCommand(TestUserId, code));
        Result replayed = await disable.HandleAsync(new TestTwoFactorCommand(TestUserId, code));

        confirmed.IsSuccess.Should().BeTrue();
        replayed.IsFailure.Should().BeTrue();
        replayed.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
        store.State!.IsTwoFactorEnabled.Should().BeTrue();
    }

    // ── Disable ──
    [Fact]
    public async Task Disable_WithAWrongCode_CountsAFailedAttempt()
    {
        var (store, service) = CreateCollaborators();
        store.SeedEnabled(service.GenerateSecret(), []);
        var protection = new RecordingLoginProtection();
        var sut = new TestDisableHandler(new FakeAuthenticator(service, store), store, protection);

        await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("000000")));

        protection.Increments.Should().Equal(AttemptKey);
        protection.Resets.Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_WhenLockedOut_IsRefusedAndLeavesTheFactorOn()
    {
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedEnabled(secret, []);
        var sut = new TestDisableHandler(
            new FakeAuthenticator(service, store), store, new RecordingLoginProtection { LockedOut = true });

        Result result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.TooManyRequests);
        store.State!.IsTwoFactorEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disable_WithALiveCode_ResetsTheFailedAttemptCounter()
    {
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedEnabled(secret, []);
        var protection = new RecordingLoginProtection();
        var sut = new TestDisableHandler(new FakeAuthenticator(service, store), store, protection);

        await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        protection.Resets.Should().Equal(AttemptKey);
    }

    [Fact]
    public async Task Disable_WithALiveCode_ClearsTheAccountsTwoFactorState()
    {
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedEnabled(secret, []);
        var sut = new TestDisableHandler(new FakeAuthenticator(service, store), store);

        Result result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsSuccess.Should().BeTrue();
        store.State!.IsTwoFactorEnabled.Should().BeFalse();
        store.State.TwoFactorSecret.Should().BeNull("a later enrollment must start from a fresh secret");
    }

    [Fact]
    public async Task Disable_WithoutACode_IsRefusedAndLeavesTheFactorOn()
    {
        // Whoever holds a stolen access token must not be able to strip the control that would have
        // stopped them.
        var (store, service) = CreateCollaborators();
        store.SeedEnabled(service.GenerateSecret(), []);
        var sut = new TestDisableHandler(new FakeAuthenticator(service, store), store);

        Result result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(string.Empty)));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorRequiredCode);
        store.State!.IsTwoFactorEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disable_OnAnAccountWithNoSecondFactor_ReportsThatRatherThanSucceeding()
    {
        var (store, service) = CreateCollaborators();
        var sut = new TestDisableHandler(new FakeAuthenticator(service, store), store);

        Result result = await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("123456")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorNotEnrolledCode);
    }

    // ── Regenerate recovery codes ──
    [Fact]
    public async Task Regenerate_ReplacesTheWholeRecoverySetRatherThanAppending()
    {
        var (store, service) = CreateCollaborators(recoveryCodeCount: 4);
        string secret = service.GenerateSecret();
        RecoveryCodeSet original = service.GenerateRecoveryCodes();
        store.SeedEnabled(secret, original.Hashes);
        var sut = new TestRegenerateHandler(new FakeAuthenticator(service, store), service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecoveryCodes.Should().HaveCount(4);
        store.State!.TwoFactorRecoveryCodeHashes.Should().HaveCount(4);
        store.State.TwoFactorRecoveryCodeHashes.Should().NotIntersectWith(
            original.Hashes,
            "a list the user believes is compromised must not survive the regenerate");
    }

    [Fact]
    public async Task Regenerate_MayBeProvedWithARecoveryCodeItIsAboutToReplace()
    {
        var (store, service) = CreateCollaborators(recoveryCodeCount: 3);
        RecoveryCodeSet original = service.GenerateRecoveryCodes();
        store.SeedEnabled(service.GenerateSecret(), original.Hashes);
        var sut = new TestRegenerateHandler(new FakeAuthenticator(service, store), service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(original.Codes[0])));

        result.IsSuccess.Should().BeTrue();
        store.State!.TwoFactorRecoveryCodeHashes.Should().NotIntersectWith(original.Hashes);
    }

    [Fact]
    public async Task Regenerate_OnAnAccountWithNoSecondFactor_IsRefused()
    {
        var (store, service) = CreateCollaborators();
        var sut = new TestRegenerateHandler(new FakeAuthenticator(service, store), service, store);

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("123456")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorNotEnrolledCode);
    }

    [Fact]
    public async Task Regenerate_WithAWrongCode_CountsAFailedAttempt()
    {
        var (store, service) = CreateCollaborators();
        store.SeedEnabled(service.GenerateSecret(), []);
        var protection = new RecordingLoginProtection();
        var sut = new TestRegenerateHandler(new FakeAuthenticator(service, store), service, store, protection);

        await sut.HandleAsync(new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest("000000")));

        protection.Increments.Should().Equal(AttemptKey);
        protection.Resets.Should().BeEmpty();
    }

    [Fact]
    public async Task Regenerate_WhenLockedOut_IsRefusedAndKeepsTheRecoverySet()
    {
        var (store, service) = CreateCollaborators(recoveryCodeCount: 2);
        string secret = service.GenerateSecret();
        RecoveryCodeSet original = service.GenerateRecoveryCodes();
        store.SeedEnabled(secret, original.Hashes);
        var sut = new TestRegenerateHandler(
            new FakeAuthenticator(service, store), service, store, new RecordingLoginProtection { LockedOut = true });

        Result<TwoFactorRecoveryCodesResponse> result = await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.TooManyRequests);
        store.State!.TwoFactorRecoveryCodeHashes.Should().BeEquivalentTo(original.Hashes);
    }

    [Fact]
    public async Task Regenerate_WithALiveCode_ResetsTheFailedAttemptCounter()
    {
        var (store, service) = CreateCollaborators();
        string secret = service.GenerateSecret();
        store.SeedEnabled(secret, []);
        var protection = new RecordingLoginProtection();
        var sut = new TestRegenerateHandler(new FakeAuthenticator(service, store), service, store, protection);

        await sut.HandleAsync(
            new TestTwoFactorCommand(TestUserId, new TwoFactorCodeRequest(StubTwoFactorService.CodeFor(secret))));

        protection.Resets.Should().Equal(AttemptKey);
    }

    private static (RecordingTwoFactorStore Store, StubTwoFactorService Service) CreateCollaborators(
        int recoveryCodeCount = 10) =>
        (new RecordingTwoFactorStore(), new StubTwoFactorService(recoveryCodeCount));
}

/// <summary>App-side two-factor command shape (target account plus the shared code payload).</summary>
public sealed record TestTwoFactorCommand(UserIdentifierType UserId, TwoFactorCodeRequest Request)
    : IUserScopedCommand<TwoFactorCodeRequest>;

/// <summary>
/// Deterministic <see cref="ITwoFactorService"/>: the code that verifies against a secret is a fixed
/// function of it, and a recovery code hashes to a readable prefix, so a test can name the exact
/// value that should work.
/// </summary>
public sealed class StubTwoFactorService(int recoveryCodeCount) : ITwoFactorService
{
    private int _secretCounter;
    private int _codeCounter;

    /// <summary>The code this stub accepts for a given secret.</summary>
    public static string CodeFor(string secret) => "code-" + secret;

    /// <inheritdoc />
    public string GenerateSecret() => string.Create(CultureInfo.InvariantCulture, $"SECRET{++_secretCounter}");

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1055:URI-return values should not be strings",
        Justification = "Matches the interface, which returns the otpauth:// value verbatim rather than through System.Uri.")]
    public string BuildProvisioningUri(string secret, string accountName) =>
        $"otpauth://totp/Test:{accountName}?secret={secret}";

    /// <inheritdoc />
    public bool VerifyCode(string secret, string? code) =>
        !string.IsNullOrWhiteSpace(code) && string.Equals(code, CodeFor(secret), StringComparison.Ordinal);

    /// <inheritdoc />
    public bool VerifyCode(string secret, string? code, out long matchedStep)
    {
        matchedStep = 0;
        return VerifyCode(secret, code);
    }

    /// <inheritdoc />
    public RecoveryCodeSet GenerateRecoveryCodes()
    {
        List<string> codes = [];
        List<string> hashes = [];

        for (int index = 0; index < recoveryCodeCount; index++)
        {
            string code = string.Create(CultureInfo.InvariantCulture, $"recovery-{++_codeCounter}");
            codes.Add(code);
            hashes.Add(HashRecoveryCode(code));
        }

        return new RecoveryCodeSet(codes, hashes);
    }

    /// <inheritdoc />
    public string HashRecoveryCode(string code) => "hash:" + code;

    /// <inheritdoc />
    public bool TryMatchRecoveryCode(string? code, IReadOnlyCollection<string> storedHashes, out string? matchedHash)
    {
        matchedHash = string.IsNullOrWhiteSpace(code)
            ? null
            : storedHashes.FirstOrDefault(hash => string.Equals(hash, HashRecoveryCode(code), StringComparison.Ordinal));

        return matchedHash is not null;
    }
}

/// <summary>
/// <see cref="ITwoFactorAuthenticator"/> mirroring the shipped one's decision order over the stub
/// service and the in-memory store, so the handler tests see realistic refusals without reaching into
/// the Infrastructure tier. Like the shipped one, it accepts a time-based code once across the
/// challenge and the enrollment-confirm check.
/// </summary>
public sealed class FakeAuthenticator(ITwoFactorService service, ITwoFactorStore store) : ITwoFactorAuthenticator
{
    private readonly HashSet<string> _acceptedCodes = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> VerifyEnrollmentCodeAsync(
        UserIdentifierType userId,
        string secret,
        string? code,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(service.VerifyCode(secret, code) && _acceptedCodes.Add(code!));

    /// <inheritdoc />
    public async Task<Result<TwoFactorOutcome>> ChallengeAsync(
        UserIdentifierType userId,
        string? code,
        CancellationToken cancellationToken = default)
    {
        var state = await store.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (state is null || !state.IsTwoFactorEnabled)
        {
            return Result.Success(TwoFactorOutcome.NotEnrolled);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorRequired());
        }

        if (state.TwoFactorSecret is { Length: > 0 } secret && service.VerifyCode(secret, code))
        {
            return _acceptedCodes.Add(code)
                ? Result.Success(TwoFactorOutcome.VerifiedTotp)
                : Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorInvalid());
        }

        if (!service.TryMatchRecoveryCode(code, state.TwoFactorRecoveryCodeHashes, out string? matched) || matched is null)
        {
            return Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorInvalid());
        }

        await store.ConsumeRecoveryCodeAsync(userId, matched, cancellationToken).ConfigureAwait(false);
        return Result.Success(TwoFactorOutcome.VerifiedRecoveryCode);
    }
}

/// <summary>In-memory <see cref="ITwoFactorStore"/> holding one account's state.</summary>
public sealed class RecordingTwoFactorStore : ITwoFactorStore
{
    /// <summary>Whether the account resolves at all.</summary>
    public bool StateExists { get; set; } = true;

    /// <summary>The account's current state.</summary>
    public ITwoFactorUserState? State { get; private set; } = new StoredState(false, null, []);

    /// <summary>Places an account in the mid-enrollment state.</summary>
    public void SeedPending(string secret) => State = new StoredState(false, secret, []);

    /// <summary>Places an account in the enrolled state.</summary>
    public void SeedEnabled(string secret, IReadOnlyCollection<string> recoveryCodeHashes) =>
        State = new StoredState(true, secret, recoveryCodeHashes);

    /// <inheritdoc />
    public Task<ITwoFactorUserState?> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(StateExists ? State : null);

    /// <inheritdoc />
    public Task<Result> StartEnrollmentAsync(UserIdentifierType userId, string secret, CancellationToken cancellationToken = default)
    {
        State = new StoredState(false, secret, []);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> CompleteEnrollmentAsync(
        UserIdentifierType userId,
        IReadOnlyCollection<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default)
    {
        State = new StoredState(true, State?.TwoFactorSecret, recoveryCodeHashes);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> DisableAsync(UserIdentifierType userId, CancellationToken cancellationToken = default)
    {
        State = new StoredState(false, null, []);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> ReplaceRecoveryCodesAsync(
        UserIdentifierType userId,
        IReadOnlyCollection<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default)
    {
        State = new StoredState(State?.IsTwoFactorEnabled ?? false, State?.TwoFactorSecret, recoveryCodeHashes);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> ConsumeRecoveryCodeAsync(
        UserIdentifierType userId,
        string recoveryCodeHash,
        CancellationToken cancellationToken = default)
    {
        var remaining = State!.TwoFactorRecoveryCodeHashes
            .Where(hash => !string.Equals(hash, recoveryCodeHash, StringComparison.Ordinal))
            .ToList();

        State = new StoredState(State.IsTwoFactorEnabled, State.TwoFactorSecret, remaining);
        return Task.FromResult(Result.Success());
    }

    private sealed record StoredState(
        bool IsTwoFactorEnabled,
        string? TwoFactorSecret,
        IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes) : ITwoFactorUserState;
}

/// <summary>Concrete begin-enrollment handler supplying the account-label hook.</summary>
public sealed class TestBeginHandler(ITwoFactorService service, ITwoFactorStore store, string? accountName)
    : BeginTwoFactorEnrollmentHandlerBase<TestTwoFactorCommand>(service, store, NullLogger.Instance)
{
    protected override Task<string?> ResolveAccountNameAsync(UserIdentifierType userId, CancellationToken cancellationToken) =>
        Task.FromResult(accountName);
}

/// <summary>
/// In-memory <see cref="ILoginProtectionService"/> recording which counter keys were incremented and
/// reset, with a switch that answers every lockout check as locked.
/// </summary>
public sealed class RecordingLoginProtection : ILoginProtectionService
{
    /// <summary>Whether every lockout check answers as locked.</summary>
    public bool LockedOut { get; init; }

    /// <summary>The keys a failed attempt was recorded against, in order.</summary>
    public List<string> Increments { get; } = [];

    /// <summary>The keys that were reset, in order.</summary>
    public List<string> Resets { get; } = [];

    /// <inheritdoc />
    public Task<Result> CheckLockoutAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(LockedOut
            ? Result.Failure(Error.TooManyRequests("Auth.TooManyAttempts", "Too many attempts."))
            : Result.Success());

    /// <inheritdoc />
    public Task IncrementFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        Increments.Add(email);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        Resets.Add(email);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result> CheckRegistrationRateLimitAsync(string? ipAddress, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    public Task IncrementRegistrationCountAsync(string? ipAddress, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Concrete confirm-enrollment handler.</summary>
public sealed class TestConfirmHandler(
    ITwoFactorService service,
    ITwoFactorStore store,
    ILoginProtectionService? loginProtection = null,
    ITwoFactorAuthenticator? authenticator = null)
    : ConfirmTwoFactorEnrollmentHandlerBase<TestTwoFactorCommand>(
        service,
        authenticator ?? new FakeAuthenticator(service, store),
        store,
        loginProtection ?? new RecordingLoginProtection(),
        NullLogger.Instance);

/// <summary>Concrete disable handler.</summary>
public sealed class TestDisableHandler(
    ITwoFactorAuthenticator authenticator,
    ITwoFactorStore store,
    ILoginProtectionService? loginProtection = null)
    : DisableTwoFactorHandlerBase<TestTwoFactorCommand>(
        authenticator, store, loginProtection ?? new RecordingLoginProtection(), NullLogger.Instance);

/// <summary>Concrete regenerate handler.</summary>
public sealed class TestRegenerateHandler(
    ITwoFactorAuthenticator authenticator,
    ITwoFactorService service,
    ITwoFactorStore store,
    ILoginProtectionService? loginProtection = null)
    : RegenerateRecoveryCodesHandlerBase<TestTwoFactorCommand>(
        authenticator, service, store, loginProtection ?? new RecordingLoginProtection(), NullLogger.Instance);
