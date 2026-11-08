using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Passkeys;

/// <summary>
/// The <see cref="IPasskeyCeremony"/> over Fido2NetLib (ADR-139): discoverable credentials, user
/// verification preferred, attestation <c>none</c> (no authenticator model information is collected).
/// Every library failure becomes a <see cref="Result"/> whose message is the library's error code, for
/// the server log only.
/// </summary>
/// <param name="settings">The bound passkey settings (relying party and origins).</param>
internal sealed class Fido2PasskeyCeremony(IOptions<PasskeySettings> settings) : IPasskeyCeremony
{
    // Built on first use, so a host with passkeys disabled never reads the relying-party settings.
    private Fido2 Verifier => field ??= Create(settings.Value);

    /// <inheritdoc />
    public string CreateRegistrationOptions(byte[] userHandle, string userName, string displayName, IReadOnlyCollection<byte[]> excludeCredentialIds)
    {
        ArgumentNullException.ThrowIfNull(excludeCredentialIds);

        return Verifier.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userHandle, Name = userName, DisplayName = displayName },

            // The browser refuses to create a second passkey on an authenticator that already holds one
            // for this account.
            ExcludeCredentials = [.. excludeCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        }).ToJson();
    }

    /// <inheritdoc />
    public async Task<Result<PasskeyAttestation>> VerifyRegistrationAsync(
        string optionsJson,
        JsonElement attestation,
        Func<byte[], CancellationToken, Task<bool>> isCredentialIdUnique,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isCredentialIdUnique);

        var response = Deserialize<AuthenticatorAttestationRawResponse>(attestation);
        if (response is null)
        {
            return Refused<PasskeyAttestation>(PasskeyErrors.RegistrationFailedCode, "malformed attestation");
        }

        try
        {
            var registered = await Verifier.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = CredentialCreateOptions.FromJson(optionsJson),
                    IsCredentialIdUniqueToUserCallback = (args, token) => isCredentialIdUnique(args.CredentialId, token),
                },
                cancellationToken).ConfigureAwait(false);

            return Result.Success(new PasskeyAttestation(
                registered.Id,
                registered.PublicKey,
                registered.SignCount,
                [.. (registered.Transports ?? []).Select(TransportName)]));
        }
        catch (Fido2VerificationException exception)
        {
            return Refused<PasskeyAttestation>(PasskeyErrors.RegistrationFailedCode, exception.Code.ToString());
        }
    }

    /// <inheritdoc />
    public string CreateSignInOptions() =>
        Verifier.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        }).ToJson();

    /// <inheritdoc />
    public byte[]? ReadCredentialId(JsonElement assertion) =>
        Deserialize<AuthenticatorAssertionRawResponse>(assertion)?.RawId is { Length: > 0 } rawId ? rawId : null;

    /// <inheritdoc />
    public async Task<Result<uint>> VerifyAssertionAsync(
        string optionsJson,
        JsonElement assertion,
        PasskeyStoredKey credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var response = Deserialize<AuthenticatorAssertionRawResponse>(assertion);
        if (response is null)
        {
            return Refused<uint>(PasskeyErrors.AssertionFailedCode, "malformed assertion");
        }

        try
        {
            var verified = await Verifier.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = AssertionOptions.FromJson(optionsJson),
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                        Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(credential.UserHandle)),
                },
                cancellationToken).ConfigureAwait(false);

            return Result.Success(verified.SignCount);
        }
        catch (Fido2VerificationException exception)
        {
            return Refused<uint>(PasskeyErrors.AssertionFailedCode, exception.Code.ToString());
        }
    }

    private static Fido2 Create(PasskeySettings options) =>
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

    private static Result<T> Refused<T>(string code, string reason) =>
        Result.Failure<T>(Error.Validation(code, reason, nameof(Fido2PasskeyCeremony)));

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
}
