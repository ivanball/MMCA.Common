namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>A verified passkey registration (ADR-139): what <see cref="IPasskeyCeremony.VerifyRegistrationAsync"/> hands back to be stored.</summary>
/// <param name="CredentialId">The WebAuthn credential id the authenticator chose.</param>
/// <param name="PublicKey">The COSE-encoded public key.</param>
/// <param name="SignCount">The signature counter the registration reported.</param>
/// <param name="Transports">The transport hints in their WebAuthn spelling ("internal", "hybrid" ...).</param>
#pragma warning disable CA1819 // byte[] is the shape the WebAuthn library and the EF varbinary column both take (the IAuthUser precedent).
public sealed record PasskeyAttestation(byte[] CredentialId, byte[] PublicKey, uint SignCount, IReadOnlyList<string> Transports);
#pragma warning restore CA1819
