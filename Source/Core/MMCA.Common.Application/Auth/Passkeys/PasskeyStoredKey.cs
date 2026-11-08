namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>The stored half of a passkey an assertion is verified against (ADR-139).</summary>
/// <param name="PublicKey">The COSE-encoded public key.</param>
/// <param name="SignCount">The signature counter recorded at the previous use.</param>
/// <param name="UserHandle">The WebAuthn user handle of the owning account, which the assertion must carry.</param>
#pragma warning disable CA1819 // byte[] is the shape the WebAuthn library and the EF varbinary column both take (the IAuthUser precedent).
public sealed record PasskeyStoredKey(byte[] PublicKey, uint SignCount, byte[] UserHandle);
#pragma warning restore CA1819
