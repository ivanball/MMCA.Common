namespace MMCA.Common.Application.Users;

/// <summary>
/// A user-scoped command or query that also carries the authenticated caller, so the shared
/// owner-or-privileged-role check (<see cref="UserOwnershipRule"/>) can be applied uniformly by the
/// account deletion and data-export use cases.
/// </summary>
public interface IUserOwnedRequest : IUserScopedRequest
{
    /// <summary>The authenticated user making the request.</summary>
    UserIdentifierType CurrentUserId { get; }

    /// <summary>
    /// The role claim of the authenticated user; may be <see langword="null"/>. Callers pass the first
    /// role claim, the only one on the single-role tokens the framework mints; a host issuing
    /// multi-role tokens passes its privileged role when the caller holds it.
    /// </summary>
    string? CurrentUserRole { get; }
}
