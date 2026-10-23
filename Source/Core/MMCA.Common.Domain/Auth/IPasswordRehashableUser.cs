using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The opt-in surface that lets the shared <c>AuthenticationServiceBase&lt;TUser&gt;</c> sign-in flow
/// upgrade a stored password hash to the hasher's current format and work factor while it holds the
/// verified plaintext. An Identity module's <c>User</c> that does not implement it keeps its stored
/// hash exactly as it is.
/// <para>
/// Deliberately separate from <see cref="IPasswordChangeableUser.ChangePassword"/>: a rehash keeps the
/// same password, so it must not raise the aggregate's password-changed domain event (or anything else
/// a real password change triggers, such as revoking sessions or notifying the owner).
/// </para>
/// </summary>
public interface IPasswordRehashableUser : IAuthUser
{
    /// <summary>
    /// Replaces the stored password material with a re-derivation of the same password, without
    /// raising a password-changed event.
    /// </summary>
    /// <param name="newPasswordHash">The re-derived hash.</param>
    /// <param name="newPasswordSalt">The salt paired with <paramref name="newPasswordHash"/>.</param>
    /// <returns>A success result, or the aggregate's invariant failure.</returns>
    Result RehashPassword(byte[] newPasswordHash, byte[] newPasswordSalt);
}
