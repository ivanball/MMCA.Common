namespace MMCA.Common.Shared.Auth.Passkeys;

/// <summary>
/// One row of the caller's passkey list (ADR-139), described in the terms a person recognizes it by.
/// <para>
/// <b>Sanitized on purpose.</b> The credential id, the public key and the sign count are absent:
/// nothing a client does with a passkey row needs them, and the list stays useless to anyone who
/// reads it over the user's shoulder.
/// </para>
/// </summary>
/// <param name="Id">The row identifier, the value rename and delete are addressed to.</param>
/// <param name="Name">The label the user gave the passkey.</param>
/// <param name="CreatedOn">The UTC instant the passkey was registered.</param>
/// <param name="LastUsedOn">The UTC instant it last signed the user in, or <see langword="null"/> if never.</param>
public sealed record PasskeyResponse(Guid Id, string Name, DateTime CreatedOn, DateTime? LastUsedOn);
