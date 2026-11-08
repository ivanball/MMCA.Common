using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>
/// The facts a machine token is minted from, for one service account (ADR-140): the same standard
/// claims a person's token carries (<c>sub</c>, name, email, role and the role's permissions).
/// </summary>
/// <param name="UserId">The service account's user identifier (the token's <c>sub</c>).</param>
/// <param name="Name">The service account's display name.</param>
/// <param name="Email">The address the app's <c>User</c> carries for it (often a synthetic one).</param>
/// <param name="Role">The service account's role, as the host names it; its permissions are read from the registry.</param>
public sealed record ServiceAccount(
    UserIdentifierType UserId,
    [property: Pii] string Name,
    [property: Pii] string Email,
    string Role);
