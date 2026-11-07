namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// The facts a view-as token is minted from, for one account (ADR-137): the same standard claims a
/// sign-in token carries (<c>sub</c>, name, email, role, and the role's permissions), so the staff
/// user sees the app as the account holder does.
/// </summary>
/// <param name="UserId">The account's identifier.</param>
/// <param name="Email">The account's email address.</param>
/// <param name="Role">The account's role, as the host names it (its permissions are read from the registry).</param>
/// <param name="FullName">The account's display name.</param>
/// <param name="IsDeleted">Whether the account is soft-deleted; a deleted account is never viewed.</param>
public sealed record ViewAsSubject(
    UserIdentifierType UserId,
    string Email,
    string Role,
    string FullName,
    bool IsDeleted);
