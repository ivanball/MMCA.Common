using System.Text.Json;

namespace MMCA.Common.Shared.Auth.Passkeys;

/// <summary>
/// The first leg of a passkey ceremony (ADR-139): the WebAuthn options the browser hands to
/// <c>navigator.credentials.create</c> or <c>navigator.credentials.get</c>, and the flow the second
/// leg must name.
/// <para>
/// <b>The flow is single-use and short-lived.</b> The server keeps the challenge behind
/// <see cref="FlowId"/> for five minutes and burns it on the first completion, so a replayed
/// completion is refused even when its signature is valid.
/// </para>
/// </summary>
/// <param name="FlowId">The opaque handle of the stored challenge; the completion request echoes it.</param>
/// <param name="Options">
/// The WebAuthn options as JSON (binary members base64url-encoded, the
/// <c>PublicKeyCredentialCreationOptionsJSON</c> or <c>PublicKeyCredentialRequestOptionsJSON</c> shape).
/// </param>
public sealed record PasskeyOptionsResponse(string FlowId, JsonElement Options);
