using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth.MachineClients;

/// <summary>
/// A program allowed to obtain access tokens through the OAuth client credentials grant (ADR-140),
/// backed one to one by its own service-account user (<see cref="ServiceAccountUserId"/>), whose role
/// decides what the tokens may do.
/// <para>
/// <b>Secrets are stored as hashes only.</b> <see cref="PrimarySecretHash"/> always holds one;
/// <see cref="SecondarySecretHash"/> holds a rotated secret until it is promoted, and both are
/// accepted meanwhile so a program can switch without downtime.
/// </para>
/// <para>
/// <b>Revoked, never deleted.</b> A revoked client keeps its row (and its audit stamps) and refuses
/// every secret from then on. It is mapped only where a host opts in (<c>AddMachineClients</c> with
/// <c>MachineClients:Enabled</c>), in the Identity database alone.
/// </para>
/// </summary>
public sealed class MachineClient : AuditableBaseEntity<Guid>
{
    /// <summary>The longest name accepted, and the column width.</summary>
    public const int NameMaxLength = 100;

    private MachineClient()
    {
    }

    /// <summary>Gets the label the operator recognizes the client by.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Gets the service-account user every token of this client is minted for.</summary>
    public UserIdentifierType ServiceAccountUserId { get; private set; }

#pragma warning disable CA1819 // byte[] is EF Core's native varbinary shape (the IAuthUser and PasskeyCredential precedent).
    /// <summary>Gets the SHA-256 hash of the primary secret.</summary>
    public byte[] PrimarySecretHash { get; private set; } = [];

    /// <summary>Gets the SHA-256 hash of the rotated secret awaiting promotion, or <see langword="null"/>.</summary>
    public byte[]? SecondarySecretHash { get; private set; }
#pragma warning restore CA1819

    /// <summary>Gets the UTC instant the client's secrets stop working, or <see langword="null"/> for no expiry.</summary>
    public DateTime? SecretExpiresOn { get; private set; }

    /// <summary>Gets the UTC instant the client was revoked, or <see langword="null"/> while it is active.</summary>
    public DateTime? RevokedOn { get; private set; }

    /// <summary>Gets the UTC instant the client last obtained a token, or <see langword="null"/> if never.</summary>
    public DateTime? LastUsedOn { get; private set; }

    /// <summary>Gets a value indicating whether the client has been revoked.</summary>
    public bool IsRevoked => RevokedOn is not null;

    /// <summary>Creates a client over an already provisioned service account.</summary>
    /// <param name="name">The label, 1 to <see cref="NameMaxLength"/> characters after trimming.</param>
    /// <param name="serviceAccountUserId">The service-account user the client's tokens are minted for.</param>
    /// <param name="primarySecretHash">The hash of the secret handed to the operator (<see cref="MachineClientSecret.Hash"/>).</param>
    /// <param name="secretExpiresOn">The UTC instant the secrets stop working, or <see langword="null"/>.</param>
    /// <param name="utcNow">The current UTC instant, against which <paramref name="secretExpiresOn"/> must lie in the future.</param>
    /// <returns>The client, or a validation failure.</returns>
    public static Result<MachineClient> Create(
        string name,
        UserIdentifierType serviceAccountUserId,
        byte[] primarySecretHash,
        DateTime? secretExpiresOn,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(primarySecretHash);

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
        {
            return Result.Failure<MachineClient>(Error.Validation(
                "MachineClients.NameInvalid", "A machine client name of 1 to 100 characters is required.", nameof(Create), nameof(name)));
        }

        if (primarySecretHash.Length != MachineClientSecret.HashByteLength)
        {
            return Result.Failure<MachineClient>(Error.Validation(
                "MachineClients.SecretHashInvalid", "A machine client secret hash must be a SHA-256 hash.", nameof(Create), nameof(primarySecretHash)));
        }

        if (secretExpiresOn is { } expiry && expiry <= utcNow)
        {
            return Result.Failure<MachineClient>(Error.Validation(
                "MachineClients.SecretExpiryInvalid", "A machine client secret expiry must lie in the future.", nameof(Create), nameof(secretExpiresOn)));
        }

        return Result.Success(new MachineClient
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            ServiceAccountUserId = serviceAccountUserId,
            PrimarySecretHash = [.. primarySecretHash],
            SecretExpiresOn = secretExpiresOn,
        });
    }

    /// <summary>
    /// Whether <paramref name="presentedSecret"/> authenticates this client at <paramref name="utcNow"/>:
    /// the client is not revoked, its secrets have not expired, and the secret matches the primary or
    /// the secondary hash. Both hashes are always compared, each in constant time.
    /// </summary>
    /// <param name="presentedSecret">The secret the program presented.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <returns><see langword="true"/> when the secret is accepted.</returns>
    public bool Accepts(string? presentedSecret, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(presentedSecret))
        {
            return false;
        }

        var presented = MachineClientSecret.Hash(presentedSecret);
        var primary = MachineClientSecret.Matches(presented, PrimarySecretHash);
        var secondary = MachineClientSecret.Matches(presented, SecondarySecretHash);

        // Both comparisons have already run, so the short-circuit below leaks no timing.
        return (primary || secondary) && !IsRevoked && (SecretExpiresOn is not { } expiry || utcNow < expiry);
    }

    /// <summary>
    /// Stores a newly issued secret beside the primary one. Both are accepted until <see cref="Promote"/>
    /// retires the primary; a second rotation before that replaces the waiting secret.
    /// </summary>
    /// <param name="secondarySecretHash">The hash of the new secret.</param>
    /// <returns>A success result, or <c>MachineClients.Revoked</c>.</returns>
    public Result Rotate(byte[] secondarySecretHash)
    {
        ArgumentNullException.ThrowIfNull(secondarySecretHash);
        if (IsRevoked)
        {
            return Result.Failure(Revoked(nameof(Rotate)));
        }

        if (secondarySecretHash.Length != MachineClientSecret.HashByteLength)
        {
            return Result.Failure(Error.Validation(
                "MachineClients.SecretHashInvalid", "A machine client secret hash must be a SHA-256 hash.", nameof(Rotate), nameof(secondarySecretHash)));
        }

        SecondarySecretHash = [.. secondarySecretHash];
        return Result.Success();
    }

    /// <summary>Makes the rotated secret the primary one; the old primary stops working.</summary>
    /// <returns>A success result, or <c>MachineClients.Revoked</c> / <c>MachineClients.NoSecondarySecret</c>.</returns>
    public Result Promote()
    {
        if (IsRevoked)
        {
            return Result.Failure(Revoked(nameof(Promote)));
        }

        if (SecondarySecretHash is null)
        {
            return Result.Failure(Error.Conflict(
                "MachineClients.NoSecondarySecret", "The machine client has no rotated secret to promote.", nameof(Promote)));
        }

        PrimarySecretHash = SecondarySecretHash;
        SecondarySecretHash = null;
        return Result.Success();
    }

    /// <summary>
    /// Revokes the client: every secret is refused from <paramref name="utcNow"/> on. Revoking a revoked
    /// client succeeds and keeps the first instant.
    /// </summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public void Revoke(DateTime utcNow) => RevokedOn ??= utcNow;

    /// <summary>Records that the client just obtained a token.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public void RecordUse(DateTime utcNow) => LastUsedOn = utcNow;

    private static Error Revoked(string source) =>
        Error.Conflict("MachineClients.Revoked", "The machine client has been revoked.", source);
}
