namespace MMCA.Common.Shared.Auth.MachineClients;

/// <summary>
/// One machine client as the administration surface lists it (ADR-140). The secret hashes are absent:
/// nothing an operator does with a client needs them.
/// </summary>
/// <param name="ClientId">The client identifier a program presents as <c>client_id</c>.</param>
/// <param name="Name">The label the operator gave the client.</param>
/// <param name="ServiceAccountUserId">The service-account user the client's tokens are minted for.</param>
/// <param name="CreatedOn">The UTC instant the client was created.</param>
/// <param name="SecretExpiresOn">The UTC instant the client's secrets stop working, or <see langword="null"/> for no expiry.</param>
/// <param name="RevokedOn">The UTC instant the client was revoked, or <see langword="null"/> while it is active.</param>
/// <param name="LastUsedOn">The UTC instant the client last obtained a token, or <see langword="null"/> if never.</param>
/// <param name="HasSecondarySecret">Whether a rotated secret is waiting to be promoted.</param>
public sealed record MachineClientResponse(
    Guid ClientId,
    string Name,
    UserIdentifierType ServiceAccountUserId,
    DateTime CreatedOn,
    DateTime? SecretExpiresOn,
    DateTime? RevokedOn,
    DateTime? LastUsedOn,
    bool HasSecondarySecret);
