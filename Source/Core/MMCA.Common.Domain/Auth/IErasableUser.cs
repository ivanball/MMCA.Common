using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The erasure surface an Identity module's <c>User</c> aggregate exposes to the shared
/// <c>DeleteUserHandlerBase&lt;TUser, TCommand&gt;</c> workflow: soft-delete the row, then
/// irreversibly anonymize the personal data it still holds (anonymize-in-place, ADR-005).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Delete"/> is redeclared here so the shared workflow calls it <b>through this
/// interface</b>: the call runs whatever <c>Delete()</c> the app <c>User</c> maps onto it, an
/// override that adds account-specific behavior included.
/// </para>
/// <para>
/// The base entity deliberately does not implement this interface, so a consumer that forgets to
/// add it fails the generic constraint at compile time rather than losing behavior at run time.
/// </para>
/// </remarks>
public interface IErasableUser : IAnonymizable
{
    /// <summary>
    /// Soft-deletes the account (sets <c>IsDeleted</c>), plus whatever the app couples to deletion.
    /// Revoking the account's refresh sessions is not part of it: the shared workflow does that
    /// through <c>IRefreshSessionStore</c> when the host registers one.
    /// </summary>
    /// <returns>A success result, or a failure if the account is already deleted.</returns>
    Result Delete();
}
