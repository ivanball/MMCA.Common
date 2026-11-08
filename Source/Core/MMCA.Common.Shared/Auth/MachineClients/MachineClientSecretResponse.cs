namespace MMCA.Common.Shared.Auth.MachineClients;

/// <summary>
/// A machine client together with a secret that was just issued for it (ADR-140): the answer to a
/// create (the primary secret) or a rotate (the secondary secret).
/// <para>
/// <b>Shown once.</b> Only the secret's SHA-256 hash is stored, so this response is the only place
/// the plaintext ever appears; a client that loses it rotates.
/// </para>
/// </summary>
/// <param name="Client">The client.</param>
/// <param name="ClientSecret">The new secret, base64url-encoded.</param>
public sealed record MachineClientSecretResponse(MachineClientResponse Client, string ClientSecret);
