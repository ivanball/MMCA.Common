using System.Text.Json;

namespace MMCA.Common.Shared.Auth.Passkeys;

/// <summary>
/// The second leg of a passkey sign-in (ADR-139): the assertion the browser returned for the options
/// of <see cref="FlowId"/>. Anonymous: the credential itself names the account (a discoverable
/// credential carries its user handle).
/// </summary>
/// <param name="FlowId">The flow the sign-in options were issued under.</param>
/// <param name="Credential">
/// The <c>PublicKeyCredential</c> from <c>navigator.credentials.get</c> as JSON (binary members
/// base64url-encoded, the <c>AuthenticationResponseJSON</c> shape).
/// </param>
public sealed record PasskeySignInRequest(string FlowId, JsonElement Credential);
