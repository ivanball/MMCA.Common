using System.Text.Json;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The WebAuthn cryptography of a passkey ceremony (ADR-139): building the options a browser signs and
/// verifying what it returns. The framework's implementation (Fido2NetLib) ships in the opt-in
/// <c>MMCA.Common.Passkeys</c> package and is registered by its <c>AddPasskeys</c>, so a host that never
/// references that package carries no WebAuthn library at all and <see cref="IPasskeyService"/> stays
/// disabled.
/// </summary>
/// <remarks>
/// Options and credentials cross this boundary as JSON in the WebAuthn <c>*JSON</c> shapes, so nothing
/// outside the implementation names a library type. A failure's <see cref="Error.Message"/> is a
/// diagnostic reason for the server log (a library error code, say); <see cref="PasskeyService"/>
/// never shows it to the caller.
/// </remarks>
public interface IPasskeyCeremony
{
    /// <summary>Builds registration (creation) options for a discoverable passkey.</summary>
    /// <param name="userHandle">The opaque WebAuthn user handle of the account.</param>
    /// <param name="userName">The account name the authenticator shows.</param>
    /// <param name="displayName">The friendly name the authenticator shows.</param>
    /// <param name="excludeCredentialIds">The account's existing credential ids, so an authenticator is not enrolled twice.</param>
    /// <returns>The creation options as JSON.</returns>
    string CreateRegistrationOptions(byte[] userHandle, string userName, string displayName, IReadOnlyCollection<byte[]> excludeCredentialIds);

    /// <summary>Verifies an attestation against the options it answers.</summary>
    /// <param name="optionsJson">The creation options exactly as issued.</param>
    /// <param name="attestation">The browser's attestation (the RegistrationResponseJSON shape).</param>
    /// <param name="isCredentialIdUnique">Answers whether a credential id is not yet registered to any account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified credential, or a failure carrying the diagnostic reason.</returns>
    Task<Result<PasskeyAttestation>> VerifyRegistrationAsync(
        string optionsJson,
        JsonElement attestation,
        Func<byte[], CancellationToken, Task<bool>> isCredentialIdUnique,
        CancellationToken cancellationToken = default);

    /// <summary>Builds sign-in (request) options that name no credential, for a discoverable sign-in.</summary>
    /// <returns>The request options as JSON.</returns>
    string CreateSignInOptions();

    /// <summary>Reads the credential id an assertion names, or <see langword="null"/> when the assertion is malformed.</summary>
    /// <param name="assertion">The browser's assertion (the AuthenticationResponseJSON shape).</param>
    /// <returns>The credential id, or <see langword="null"/>.</returns>
    byte[]? ReadCredentialId(JsonElement assertion);

    /// <summary>
    /// Verifies an assertion against the options it answers and the stored public key, including the
    /// user handle and the signature counter (a counter that does not advance on a counting authenticator
    /// is refused).
    /// </summary>
    /// <param name="optionsJson">The request options exactly as issued.</param>
    /// <param name="assertion">The browser's assertion.</param>
    /// <param name="credential">The stored public key, counter and owning user handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counter the authenticator reported, or a failure carrying the diagnostic reason.</returns>
    Task<Result<uint>> VerifyAssertionAsync(
        string optionsJson,
        JsonElement assertion,
        PasskeyStoredKey credential,
        CancellationToken cancellationToken = default);
}
