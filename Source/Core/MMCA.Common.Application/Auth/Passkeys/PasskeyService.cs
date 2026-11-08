using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The passkey ceremonies (ADR-139): challenges, the passkey store, and the hand-off to the token
/// workflow. The WebAuthn cryptography is behind <see cref="IPasskeyCeremony"/> and the single-use
/// challenges behind <see cref="PasskeyChallenges"/>; all three are registered together by
/// <c>AddPasskeys</c> in the opt-in <c>MMCA.Common.Passkeys</c> package.
/// <para>
/// <b>Failures are vague to the caller and specific in the log.</b> An unknown credential, a bad
/// signature and a counter that did not advance all answer <see cref="PasskeyErrors.AssertionFailed"/>;
/// the reason is logged as a Warning with no credential bytes, key or token in it. Login protection is
/// keyed by email, which a discoverable sign-in does not have, so failures are metered by the
/// <c>auth-ip</c> rate-limit policy on the endpoint instead.
/// </para>
/// </summary>
/// <param name="settings">The bound passkey settings.</param>
/// <param name="ceremony">The WebAuthn options builder and verifier.</param>
/// <param name="store">The passkey rows.</param>
/// <param name="challenges">The single-use challenges and the per-account delete lock.</param>
/// <param name="authenticationService">Mints the token pair for a verified sign-in, and answers the last-passkey question.</param>
/// <param name="timeProvider">The clock passkey instants are stamped from.</param>
/// <param name="logger">Receives the reason for every refused ceremony.</param>
public sealed partial class PasskeyService(
    IOptions<PasskeySettings> settings,
    IPasskeyCeremony ceremony,
    IPasskeyCredentialStore store,
    PasskeyChallenges challenges,
    IAuthenticationService authenticationService,
    TimeProvider timeProvider,
    ILogger<PasskeyService> logger) : IPasskeyService
{
    /// <inheritdoc />
    public bool IsEnabled => settings.Value.Enabled;

    /// <summary>The WebAuthn user handle of an account: its id as UTF-8, opaque and free of personal data.</summary>
    /// <param name="userId">The account.</param>
    /// <returns>The user handle.</returns>
    public static byte[] UserHandleFor(UserIdentifierType userId) =>
        Encoding.UTF8.GetBytes(userId.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public async Task<Result<PasskeyOptionsResponse>> BeginRegistrationAsync(
        UserIdentifierType userId,
        string userName,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var optionsJson = ceremony.CreateRegistrationOptions(
            UserHandleFor(userId),
            userName,
            displayName,
            [.. existing.Select(c => c.CredentialId)]);

        var flowId = await challenges
            .IssueAsync(new PasskeyChallenge(PasskeyChallenge.Register, userId, optionsJson), cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(new PasskeyOptionsResponse(flowId, ParseJson(optionsJson)));
    }

    /// <inheritdoc />
    public async Task<Result<PasskeyResponse>> CompleteRegistrationAsync(
        UserIdentifierType userId,
        PasskeyRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var challenge = await challenges.RedeemAsync(request.FlowId, cancellationToken).ConfigureAwait(false);
        if (challenge is null
            || !string.Equals(challenge.Ceremony, PasskeyChallenge.Register, StringComparison.Ordinal)
            || challenge.UserId != userId)
        {
            return Result.Failure<PasskeyResponse>(PasskeyErrors.ChallengeInvalid(nameof(CompleteRegistrationAsync)));
        }

        var verified = await ceremony
            .VerifyRegistrationAsync(
                challenge.OptionsJson,
                request.Credential,
                async (credentialId, token) => await store.FindByCredentialIdAsync(credentialId, token).ConfigureAwait(false) is null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!verified.TryGetValue(out var attestation))
        {
            LogRegistrationRefused(logger, userId, Reason(verified));
            return Result.Failure<PasskeyResponse>(PasskeyErrors.RegistrationFailed(nameof(CompleteRegistrationAsync)));
        }

        var created = PasskeyCredential.Create(
            userId,
            attestation.CredentialId,
            attestation.PublicKey,
            attestation.SignCount,
            attestation.Transports,
            request.Name,
            timeProvider.GetUtcNow().UtcDateTime);
        if (!created.TryGetValue(out var credential))
        {
            return Result.Failure<PasskeyResponse>(created.Errors);
        }

        await store.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        return Result.Success(ToResponse(credential));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PasskeyResponse>>> ListAsync(UserIdentifierType userId, CancellationToken cancellationToken = default)
    {
        var credentials = await store.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<PasskeyResponse>>([.. credentials.Select(ToResponse)]);
    }

    /// <inheritdoc />
    public async Task<Result<PasskeyResponse>> RenameAsync(
        UserIdentifierType userId,
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        var credential = await store.FindAsync(userId, id, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return Result.Failure<PasskeyResponse>(PasskeyErrors.NotFound(nameof(RenameAsync)));
        }

        var renamed = credential.Rename(name);
        if (renamed.IsFailure)
        {
            return Result.Failure<PasskeyResponse>(renamed.Errors);
        }

        await store.UpdateAsync(credential, cancellationToken).ConfigureAwait(false);
        return Result.Success(ToResponse(credential));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The count and the delete run inside the account's passkey lock
    /// (<see cref="PasskeyChallenges.LockAccountAsync"/>), so two concurrent deletes of an account's last
    /// two passkeys cannot both see "one more remains".
    /// </remarks>
    public async Task<Result> DeleteAsync(UserIdentifierType userId, Guid id, CancellationToken cancellationToken = default)
    {
        var handle = await challenges.LockAccountAsync(userId, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            return Result.Failure(Error.Conflict("Passkeys.Busy", "Another change to your passkeys is in progress. Try again.", nameof(DeleteAsync)));
        }

        await using (handle.ConfigureAwait(false))
        {
            var credential = await store.FindAsync(userId, id, cancellationToken).ConfigureAwait(false);
            if (credential is null)
            {
                return Result.Failure(PasskeyErrors.NotFound(nameof(DeleteAsync)));
            }

            var all = await store.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);
            if (all.Count <= 1
                && !await authenticationService.HasSignInMethodBesidesPasskeysAsync(userId, cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure(PasskeyErrors.LastPasskey(nameof(DeleteAsync)));
            }

            await store.RemoveAsync(credential, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
    }

    /// <inheritdoc />
    public async Task<Result<PasskeyOptionsResponse>> BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        // No allowed credentials: the browser offers whichever discoverable passkey the user holds for
        // this relying party, so no account is named before the assertion.
        var optionsJson = ceremony.CreateSignInOptions();
        var flowId = await challenges
            .IssueAsync(new PasskeyChallenge(PasskeyChallenge.SignIn, UserId: null, optionsJson), cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(new PasskeyOptionsResponse(flowId, ParseJson(optionsJson)));
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> CompleteSignInAsync(
        PasskeySignInRequest request,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var challenge = await challenges.RedeemAsync(request.FlowId, cancellationToken).ConfigureAwait(false);
        if (challenge is null || !string.Equals(challenge.Ceremony, PasskeyChallenge.SignIn, StringComparison.Ordinal))
        {
            LogAssertionRefused(logger, null, "unknown, expired or already used challenge");
            return Result.Failure<AuthenticationResponse>(PasskeyErrors.ChallengeInvalid(nameof(CompleteSignInAsync)));
        }

        var verified = await VerifyAssertionAsync(request.Credential, challenge.OptionsJson, cancellationToken).ConfigureAwait(false);
        if (verified is null)
        {
            return Result.Failure<AuthenticationResponse>(PasskeyErrors.AssertionFailed(nameof(CompleteSignInAsync)));
        }

        return await authenticationService
            .SignInWithPasskeyAsync(verified.UserId, ipAddress, userAgent, cancellationToken)
            .ConfigureAwait(false);
    }

    private static PasskeyResponse ToResponse(PasskeyCredential credential) =>
        new(credential.Id, credential.Name, credential.CreatedOn, credential.LastUsedOn);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Reason(Result failed) =>
        failed.Errors is [var first, ..] ? first.Message : "unspecified";

    /// <summary>
    /// Verifies an assertion against the stored public key and counter, records the use, and returns
    /// the passkey; <see langword="null"/> (with a Warning) for every refusal.
    /// </summary>
    private async Task<PasskeyCredential?> VerifyAssertionAsync(JsonElement json, string optionsJson, CancellationToken cancellationToken)
    {
        if (ceremony.ReadCredentialId(json) is not { Length: > 0 } credentialId)
        {
            LogAssertionRefused(logger, null, "malformed assertion");
            return null;
        }

        var credential = await store.FindByCredentialIdAsync(credentialId, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            LogAssertionRefused(logger, null, "unknown credential");
            return null;
        }

        var verified = await ceremony
            .VerifyAssertionAsync(
                optionsJson,
                json,
                new PasskeyStoredKey(credential.PublicKey, (uint)credential.SignCount, UserHandleFor(credential.UserId)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!verified.TryGetValue(out var signCount))
        {
            LogAssertionRefused(logger, credential.Id, Reason(verified));
            return null;
        }

        // Checked here as well as by the ceremony, so the rule does not depend on the library: a counter
        // that did not advance on a counting authenticator means a cloned key.
        var used = credential.RecordUse(signCount, timeProvider.GetUtcNow().UtcDateTime);
        if (used.IsFailure)
        {
            LogAssertionRefused(logger, credential.Id, "signature counter did not advance");
            return null;
        }

        await store.UpdateAsync(credential, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passkey registration refused for user {UserId}: {Reason}")]
    private static partial void LogRegistrationRefused(ILogger logger, UserIdentifierType userId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passkey sign-in refused (passkey {PasskeyId}): {Reason}")]
    private static partial void LogAssertionRefused(ILogger logger, Guid? passkeyId, string reason);
}
