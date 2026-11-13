namespace MMCA.Common.Shared.Webhooks;

/// <summary>
/// A webhook subscription together with a signing secret that was just issued for it: the answer to a
/// create or a rotate.
/// <para>
/// <b>Shown once.</b> Only the protected secret is stored, so this response is the only place the
/// plaintext appears; a receiver that loses it rotates.
/// </para>
/// </summary>
/// <param name="Subscription">The subscription.</param>
/// <param name="Secret">The new signing secret (<c>whsec_</c> followed by the Base64 key).</param>
public sealed record WebhookSubscriptionSecretResponse(WebhookSubscriptionResponse Subscription, string Secret);
