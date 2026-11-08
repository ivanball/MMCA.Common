namespace MMCA.Common.Shared.Auth.Passkeys;

/// <summary>Renames one of the caller's passkeys (ADR-139).</summary>
/// <param name="Name">The new label, 1 to 100 characters after trimming.</param>
public sealed record PasskeyRenameRequest(string Name);
