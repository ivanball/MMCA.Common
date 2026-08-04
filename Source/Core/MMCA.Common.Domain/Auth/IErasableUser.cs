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
/// <see cref="Delete"/> is redeclared here rather than reached through
/// <c>AuditableBaseEntity&lt;TId&gt;.Delete()</c> on purpose, and the distinction is load-bearing.
/// An app <c>User</c> may <b>hide</b> the base method (<c>public new Result Delete()</c>) to add
/// account-specific behavior such as revoking the refresh token, and a hidden method is not an
/// override: a shared workflow that called the base member through the entity constraint would
/// silently run the base implementation and skip that behavior. Because the app <c>User</c> lists
/// this interface in its own base list, the interface map resolves to the most derived
/// <c>Delete()</c> declared on the app type, so the shared workflow calls exactly the member the
/// pre-hoist handler called. The workflow must invoke it <b>through this interface</b> (member lookup
/// on a generic type parameter prefers the members of its class constraint, which would silently
/// select the hidden base method).
/// </para>
/// <para>
/// The base entity deliberately does not implement this interface, so a consumer that forgets to
/// add it fails the generic constraint at compile time rather than losing behavior at run time.
/// </para>
/// </remarks>
public interface IErasableUser : IAnonymizable
{
    /// <summary>
    /// Soft-deletes the account (sets <c>IsDeleted</c>), plus whatever the app couples to deletion
    /// (typically revoking the refresh token so outstanding sessions die immediately).
    /// </summary>
    /// <returns>A success result, or a failure if the account is already deleted.</returns>
    Result Delete();
}
