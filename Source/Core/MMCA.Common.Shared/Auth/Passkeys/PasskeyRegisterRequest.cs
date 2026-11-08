using System.Text.Json;

namespace MMCA.Common.Shared.Auth.Passkeys;

/// <summary>
/// The second leg of a passkey registration (ADR-139): the attestation the browser returned for the
/// options of <see cref="FlowId"/>, and the name the user gave the passkey.
/// </summary>
/// <param name="FlowId">The flow the registration options were issued under.</param>
/// <param name="Credential">
/// The <c>PublicKeyCredential</c> from <c>navigator.credentials.create</c> as JSON (binary members
/// base64url-encoded, the <c>RegistrationResponseJSON</c> shape).
/// </param>
/// <param name="Name">
/// A label the user recognizes the passkey by ("Work laptop"). Optional; a blank name becomes a
/// default label.
/// </param>
public sealed record PasskeyRegisterRequest(string FlowId, JsonElement Credential, string? Name = null);
