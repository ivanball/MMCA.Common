using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Infrastructure.Auth.Passkeys;

/// <summary>
/// The passkey ceremonies (ADR-139), verified by Fido2NetLib.
/// <para>
/// <b>Challenges are single-use and short-lived.</b> Each options call stores its options under a
/// random flow id in the shared cache for <see cref="ChallengeLifetime"/>; the completion reads and
/// burns that entry inside <c>lock:passkeys:challenge:{flowId}</c> (the OAuth exchange-code pattern),
/// so a completion replayed on any replica finds nothing and is refused, whatever its signature.
/// </para>
/// <para>
/// <b>Failures are vague to the caller and specific in the log.</b> An unknown credential, a bad
/// signature and a counter that did not advance all answer <see cref="PasskeyErrors.AssertionFailed"/>;
/// the reason is logged as a Warning with no credential bytes, key or token in it. Login protection is
/// keyed by email, which a discoverable sign-in does not have, so failures are metered by the
/// <c>auth-ip</c> rate-limit policy on the endpoint instead.
/// </para>
/// </summary>
internal sealed partial class PasskeyService(
    IOptions<PasskeySettings> settings,
    IPasskeyCredentialStore store,
    ICacheService cache,
    IDistributedLock distributedLock,
    IAuthenticationService authenticationService,
    TimeProvider timeProvider,
    ILogger<PasskeyService> logger) : IPasskeyService
{
    /// <summary>How long an options response stays redeemable.</summary>
    internal static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private const string ChallengeKeyPrefix = "passkeys:challenge:";
    private const string RegisterCeremony = "register";
    private const string SignInCeremony = "signin";
    private const int FlowIdLength = 32;

    // The critical sections are one cache read plus one remove, or one count plus one delete, so the
    // lease and wait of the OAuth exchange-code lock fit them too.
    private static readonly TimeSpan LockTimeToLive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public bool IsEnabled => settings.Value.Enabled;

    // Built on first use, so a host with passkeys disabled never reads the relying-party settings.
    private Fido2 Verifier => field ??= CreateFido2(settings.Value);

    /// <inheritdoc />
    public async Task<Result<PasskeyOptionsResponse>> BeginRegistrationAsync(
        UserIdentifierType userId,
        string userName,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);

        var options = Verifier.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(userId), Name = userName, DisplayName = displayName },

            // The browser refuses to create a second passkey on an authenticator that already holds one
            // for this account.
            ExcludeCredentials = [.. existing.Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))],

            // Discoverable, so the sign-in needs no username first; attestation none, so no authenticator
            // model information is collected.
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var optionsJson = options.ToJson();
        var flowId = await StoreChallengeAsync(new PasskeyChallenge(RegisterCeremony, userId, optionsJson), cancellationToken).ConfigureAwait(false);
        return Result.Success(new PasskeyOptionsResponse(flowId, ParseJson(optionsJson)));
    }

    /// <inheritdoc />
    public async Task<Result<PasskeyResponse>> CompleteRegistrationAsync(
        UserIdentifierType userId,
        PasskeyRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var challenge = await RedeemChallengeAsync(request.FlowId, cancellationToken).ConfigureAwait(false);
        if (challenge is null
            || !string.Equals(challenge.Ceremony, RegisterCeremony, StringComparison.Ordinal)
            || challenge.UserId != userId)
        {
            return Result.Failure<PasskeyResponse>(PasskeyErrors.ChallengeInvalid(nameof(CompleteRegistrationAsync)));
        }

        var attestation = Deserialize<AuthenticatorAttestationRawResponse>(request.Credential);
        if (attestation is null)
        {
            LogRegistrationRefused(logger, userId, "malformed attestation");
            return Result.Failure<PasskeyResponse>(PasskeyErrors.RegistrationFailed(nameof(CompleteRegistrationAsync)));
        }

        RegisteredPublicKeyCredential registered;
        try
        {
            registered = await Verifier.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = attestation,
                    OriginalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson),
                    IsCredentialIdUniqueToUserCallback = async (args, token) =>
                        await store.FindByCredentialIdAsync(args.CredentialId, token).ConfigureAwait(false) is null,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException exception)
        {
            LogRegistrationRefused(logger, userId, exception.Code.ToString());
            return Result.Failure<PasskeyResponse>(PasskeyErrors.RegistrationFailed(nameof(CompleteRegistrationAsync)));
        }

        var created = PasskeyCredential.Create(
            userId,
            registered.Id,
            registered.PublicKey,
            registered.SignCount,
            registered.Transports?.Select(TransportName),
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
    /// The count and the delete run inside a per-account lock, so two concurrent deletes of an
    /// account's last two passkeys cannot both see "one more remains".
    /// </remarks>
    public async Task<Result> DeleteAsync(UserIdentifierType userId, Guid id, CancellationToken cancellationToken = default)
    {
        var handle = await distributedLock
            .TryAcquireAsync(string.Create(CultureInfo.InvariantCulture, $"lock:passkeys:user:{userId}"), LockTimeToLive, LockWait, cancellationToken)
            .ConfigureAwait(false);
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
        var options = Verifier.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var optionsJson = options.ToJson();
        var flowId = await StoreChallengeAsync(new PasskeyChallenge(SignInCeremony, UserId: null, optionsJson), cancellationToken).ConfigureAwait(false);
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

        var challenge = await RedeemChallengeAsync(request.FlowId, cancellationToken).ConfigureAwait(false);
        if (challenge is null || !string.Equals(challenge.Ceremony, SignInCeremony, StringComparison.Ordinal))
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

    private static Fido2 CreateFido2(PasskeySettings options) =>
        new(
            new Fido2Configuration
            {
                RPID = options.RelyingPartyId,
                RPName = options.RelyingPartyName,
                Origins = new HashSet<string>(
                    options.Origins.Select(origin => new Uri(origin).GetLeftPart(UriPartial.Authority)),
                    StringComparer.OrdinalIgnoreCase),
            },
            metadataService: null);

    /// <summary>The WebAuthn user handle of an account: its id as UTF-8, opaque and free of personal data.</summary>
    private static byte[] UserHandle(UserIdentifierType userId) =>
        Encoding.UTF8.GetBytes(userId.ToString(CultureInfo.InvariantCulture));

    private static PasskeyResponse ToResponse(PasskeyCredential credential) =>
        new(credential.Id, credential.Name, credential.CreatedOn, credential.LastUsedOn);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static T? Deserialize<T>(JsonElement json)
        where T : class
    {
        try
        {
            return json.ValueKind == JsonValueKind.Object ? json.Deserialize<T>() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The spelling the WebAuthn AuthenticatorTransport enumeration uses, which the browser reports back.
    private static string TransportName(AuthenticatorTransport transport) => transport switch
    {
        AuthenticatorTransport.Usb => "usb",
        AuthenticatorTransport.Nfc => "nfc",
        AuthenticatorTransport.Ble => "ble",
        AuthenticatorTransport.SmartCard => "smart-card",
        AuthenticatorTransport.Hybrid => "hybrid",
        AuthenticatorTransport.Internal => "internal",
        _ => transport.ToString(),
    };

    private static bool IsWellFormedFlowId(string? flowId) =>
        flowId is { Length: FlowIdLength } && flowId.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// Verifies an assertion against the stored public key and counter, records the use, and returns
    /// the passkey; <see langword="null"/> (with a Warning) for every refusal.
    /// </summary>
    private async Task<PasskeyCredential?> VerifyAssertionAsync(JsonElement json, string optionsJson, CancellationToken cancellationToken)
    {
        var assertion = Deserialize<AuthenticatorAssertionRawResponse>(json);
        if (assertion?.RawId is not { Length: > 0 } credentialId)
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

        VerifyAssertionResult result;
        try
        {
            result = await Verifier.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = assertion,
                    OriginalOptions = AssertionOptions.FromJson(optionsJson),
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = (uint)credential.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                        Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(UserHandle(credential.UserId))),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException exception)
        {
            LogAssertionRefused(logger, credential.Id, exception.Code.ToString());
            return null;
        }

        // Checked here as well as by the verifier, so the rule does not depend on the library version:
        // a counter that did not advance on a counting authenticator means a cloned key.
        var used = credential.RecordUse(result.SignCount, timeProvider.GetUtcNow().UtcDateTime);
        if (used.IsFailure)
        {
            LogAssertionRefused(logger, credential.Id, "signature counter did not advance");
            return null;
        }

        await store.UpdateAsync(credential, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    private async Task<string> StoreChallengeAsync(PasskeyChallenge challenge, CancellationToken cancellationToken)
    {
        var flowId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(FlowIdLength / 2));
        await cache
            .SetAsync(ChallengeKeyPrefix + flowId, JsonSerializer.Serialize(challenge), ChallengeLifetime, cancellationToken)
            .ConfigureAwait(false);
        return flowId;
    }

    /// <summary>Reads and burns a stored challenge in one critical section; <see langword="null"/> when there is none to redeem.</summary>
    private async Task<PasskeyChallenge?> RedeemChallengeAsync(string? flowId, CancellationToken cancellationToken)
    {
        if (!IsWellFormedFlowId(flowId))
        {
            return null;
        }

        var key = ChallengeKeyPrefix + flowId;
        var handle = await distributedLock.TryAcquireAsync("lock:" + key, LockTimeToLive, LockWait, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            // Contended: another request is redeeming this flow right now, so this one is the replay.
            return null;
        }

        await using (handle.ConfigureAwait(false))
        {
            // The shared store, not a local copy: a flow burned on another replica must be a miss here.
            var stored = await cache.GetFromSharedStoreAsync<string>(key, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PasskeyChallenge>(stored);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passkey registration refused for user {UserId}: {Reason}")]
    private static partial void LogRegistrationRefused(ILogger logger, UserIdentifierType userId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passkey sign-in refused (passkey {PasskeyId}): {Reason}")]
    private static partial void LogAssertionRefused(ILogger logger, Guid? passkeyId, string reason);

    /// <summary>One stored challenge: which ceremony it opened, for whom, and the exact options issued.</summary>
    /// <param name="Ceremony">Registration or sign-in.</param>
    /// <param name="UserId">The account a registration was opened for; <see langword="null"/> for a sign-in.</param>
    /// <param name="OptionsJson">The options as issued, which the verifier checks the response against.</param>
    internal sealed record PasskeyChallenge(string Ceremony, UserIdentifierType? UserId, string OptionsJson);
}
