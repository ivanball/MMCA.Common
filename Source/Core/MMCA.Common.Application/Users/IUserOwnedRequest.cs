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

    /// <summary>The role claim of the authenticated user; may be <see langword="null"/>.</summary>
    string? CurrentUserRole { get; }
}
