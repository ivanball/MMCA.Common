using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// One registered passkey (a WebAuthn public-key credential, ADR-139): the public half of a key pair
/// whose private half never leaves the user's authenticator, bound to one account.
/// <para>
/// <b>Framework bookkeeping, not an aggregate.</b> Like <see cref="RefreshSession"/>, a flat record
/// with no audit stamps, no soft-delete flag and no concurrency token: a row is inserted at
/// registration, touched on each sign-in (<see cref="RecordUse"/>), renamed, and deleted outright. It
/// is mapped only where a host opts in (<c>AddPasskeys</c> with <c>Passkeys:Enabled</c>), in the
/// Identity database alone.
/// </para>
/// <para>
/// <b>Nothing here is secret.</b> The credential id and the public key only verify signatures, so a
/// database read cannot sign in. The sign count is what detects a cloned authenticator.
/// </para>
/// </summary>
public sealed class PasskeyCredential
{
    /// <summary>The longest credential id WebAuthn allows, and the column width.</summary>
    public const int CredentialIdMaxLength = 1023;

    /// <summary>The column width for the COSE-encoded public key (an RSA-4096 key fits).</summary>
    public const int PublicKeyMaxLength = 2048;

    /// <summary>The longest label accepted, and the column width.</summary>
    public const int NameMaxLength = 100;

    /// <summary>The column width for the comma-separated transport hints.</summary>
    public const int TransportsMaxLength = 100;

    /// <summary>The label a passkey registered without a name gets.</summary>
    public const string DefaultName = "Passkey";

    /// <summary>Gets the row identifier, the value rename and delete are addressed to.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the account this passkey signs in.</summary>
    public required UserIdentifierType UserId { get; init; }

#pragma warning disable CA1819 // byte[] is EF Core's native varbinary shape, and the Fido2 verifier takes the key as byte[] (the IAuthUser precedent).
    /// <summary>Gets the WebAuthn credential id the authenticator chose (unique across all accounts).</summary>
    public required byte[] CredentialId { get; init; }

    /// <summary>Gets the COSE-encoded public key assertions are verified with.</summary>
    public required byte[] PublicKey { get; init; }
#pragma warning restore CA1819

    /// <summary>
    /// Gets the last signature counter the authenticator reported. Zero for an authenticator that does
    /// not count (synced passkeys report zero on every use).
    /// </summary>
    public long SignCount { get; private set; }

    /// <summary>
    /// Gets the transport hints the browser reported at registration ("internal", "hybrid",
    /// "usb" ...), comma-separated, or <see langword="null"/> when none were reported.
    /// </summary>
    public string? Transports { get; init; }

    /// <summary>Gets the label the user recognizes the passkey by.</summary>
    public string Name { get; private set; } = DefaultName;

    /// <summary>Gets the UTC instant the passkey was registered.</summary>
    public DateTime CreatedOn { get; init; }

    /// <summary>Gets the UTC instant the passkey last signed the user in, or <see langword="null"/>.</summary>
    public DateTime? LastUsedOn { get; private set; }

    /// <summary>
    /// Creates a passkey row from a verified registration.
    /// </summary>
    /// <param name="userId">The account the passkey signs in.</param>
    /// <param name="credentialId">The WebAuthn credential id.</param>
    /// <param name="publicKey">The COSE-encoded public key.</param>
    /// <param name="signCount">The signature counter the registration reported.</param>
    /// <param name="transports">The transport hints, or <see langword="null"/>.</param>
    /// <param name="name">The label; blank becomes <see cref="DefaultName"/>.</param>
    /// <param name="createdOn">The UTC instant of the registration.</param>
    /// <returns>The passkey, or a validation failure.</returns>
    public static Result<PasskeyCredential> Create(
        UserIdentifierType userId,
        byte[] credentialId,
        byte[] publicKey,
        uint signCount,
        IEnumerable<string>? transports,
        string? name,
        DateTime createdOn)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(publicKey);

        var trimmed = name?.Trim();
        var validation = ValidateMaterial(credentialId, publicKey, trimmed);
        if (validation.IsFailure)
        {
            return Result.Failure<PasskeyCredential>(validation.Errors);
        }

        return Result.Success(new PasskeyCredential
        {
            UserId = userId,
            CredentialId = [.. credentialId],
            PublicKey = [.. publicKey],
            SignCount = signCount,
            Transports = JoinTransports(transports),
            Name = string.IsNullOrEmpty(trimmed) ? DefaultName : trimmed,
            CreatedOn = createdOn,
        });
    }

    /// <summary>
    /// Whether an assertion's signature counter is acceptable after <paramref name="stored"/>: the
    /// counter must go up, except that zero on both sides is allowed (an authenticator that does not
    /// count, which is what synced passkeys do). A counter that stays put or goes down on a counting
    /// authenticator means two copies of the key are in use, so the assertion is refused.
    /// </summary>
    /// <param name="stored">The counter recorded at the previous use.</param>
    /// <param name="presented">The counter the authenticator reports now.</param>
    /// <returns><see langword="true"/> when the assertion may proceed.</returns>
    public static bool IsSignCountAcceptable(long stored, uint presented) =>
        stored == 0 && presented == 0 || presented > stored;

    /// <summary>
    /// Records a successful sign-in: the new signature counter and the instant. Refuses a counter
    /// <see cref="IsSignCountAcceptable"/> rejects, leaving the row unchanged.
    /// </summary>
    /// <param name="presentedSignCount">The counter the assertion reported.</param>
    /// <param name="usedOn">The UTC instant of the sign-in.</param>
    /// <returns>A success result, or <c>Passkeys.SignCountInvalid</c>.</returns>
    public Result RecordUse(uint presentedSignCount, DateTime usedOn)
    {
        if (!IsSignCountAcceptable(SignCount, presentedSignCount))
        {
            return Result.Failure(Error.Unauthorized(
                "Passkeys.SignCountInvalid",
                "The authenticator's signature counter did not advance.",
                nameof(RecordUse)));
        }

        SignCount = presentedSignCount;
        LastUsedOn = usedOn;
        return Result.Success();
    }

    /// <summary>Renames the passkey.</summary>
    /// <param name="name">The new label, 1 to <see cref="NameMaxLength"/> characters after trimming.</param>
    /// <returns>A success result, or <c>Passkeys.NameInvalid</c>.</returns>
    public Result Rename(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
        {
            return Result.Failure(NameInvalid(nameof(name)));
        }

        Name = trimmed;
        return Result.Success();
    }

    private static Result ValidateMaterial(byte[] credentialId, byte[] publicKey, string? trimmedName)
    {
        if (credentialId.Length == 0 || credentialId.Length > CredentialIdMaxLength)
        {
            return Result.Failure(Error.Validation(
                "Passkeys.CredentialIdInvalid", "The credential id is empty or too long.", nameof(Create), nameof(credentialId)));
        }

        if (publicKey.Length == 0 || publicKey.Length > PublicKeyMaxLength)
        {
            return Result.Failure(Error.Validation(
                "Passkeys.PublicKeyInvalid", "The public key is empty or too long.", nameof(Create), nameof(publicKey)));
        }

        return trimmedName is { Length: > NameMaxLength }
            ? Result.Failure(NameInvalid("name"))
            : Result.Success();
    }

    // Hints only: an oversized or empty list is dropped rather than refusing the passkey.
    private static string? JoinTransports(IEnumerable<string>? transports)
    {
        var joined = transports is null ? null : string.Join(',', transports);
        return string.IsNullOrEmpty(joined) || joined.Length > TransportsMaxLength ? null : joined;
    }

    private static Error NameInvalid(string target) =>
        Error.Validation("Passkeys.NameInvalid", "A passkey name of 1 to 100 characters is required.", nameof(Rename), target);
}
