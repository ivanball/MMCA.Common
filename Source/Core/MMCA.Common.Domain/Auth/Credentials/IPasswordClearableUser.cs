using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth.Credentials;

/// <summary>
/// The opt-in surface that lets the shared sign-in-link workflow remove an account's password
/// credential. A sign-in link redeemed for an UNCONFIRMED account that already holds a password
/// proves the mailbox but not the password, so the workflow confirms the address and removes the
/// password in the same save, leaving the account with no password sign-in until its owner sets one
/// (forgot password).
/// </summary>
/// <remarks>
/// <para>
/// "No password credential" is the shape an external-login account already carries: an empty
/// <see cref="IAuthUser.PasswordHash"/> and an empty <see cref="IAuthUser.PasswordSalt"/>, which
/// password sign-in never accepts. <see cref="ClearPassword"/> moves the account to that shape.
/// </para>
/// <para>
/// An account type that holds password material but does not implement this interface has its
/// sign-in-link redemption refused while it is unconfirmed: the workflow never confirms an address
/// whose existing password it cannot remove.
/// </para>
/// </remarks>
public interface IPasswordClearableUser : IAuthUser
{
    /// <summary>
    /// Replaces the stored password hash and salt with empty material, so password sign-in is no
    /// longer a path the account has.
    /// </summary>
    /// <returns>A success result, or the aggregate's invariant failure.</returns>
    Result ClearPassword();
}
