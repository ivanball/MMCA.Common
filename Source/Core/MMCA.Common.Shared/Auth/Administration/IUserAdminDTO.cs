namespace MMCA.Common.Shared.Auth.Administration;

/// <summary>
/// The projection the framework's user-administration UI reads from an app's own user DTO. ADR-116
/// keeps that DTO app-owned (the columns an operator needs differ per app), so the shared list
/// component states only the four values it renders itself and the app's DTO declares how it
/// answers them.
/// </summary>
/// <remarks>
/// Implement it on the administration DTO the app already serves from its <c>Admin/Users</c>
/// endpoints. An explicit implementation is fine and is usually the right one: the component reads
/// through the interface, so a DTO that spells the lock state its own way (ADC's <c>LockedOn</c>
/// timestamp, Store's <c>IsActive</c> flag) maps it here instead of renaming its wire contract.
/// </remarks>
public interface IUserAdminDTO
{
    /// <summary>Gets the account identifier, used for the detail route and for every administration call.</summary>
    UserIdentifierType UserId { get; }

    /// <summary>Gets the account's email address, which the list renders as the row's primary label.</summary>
    string Email { get; }

    /// <summary>Gets the single role the account holds.</summary>
    string Role { get; }

    /// <summary>
    /// Gets a value indicating whether the account is shut out of sign-in (ADC: <c>LockedOn</c> is
    /// not null; Store: <c>!IsActive</c>).
    /// </summary>
    bool IsLocked { get; }
}
