using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The password-rotation surface an Identity module's <c>User</c> aggregate exposes to the shared
/// <c>ChangePasswordHandlerBase&lt;TUser, TCommand&gt;</c> workflow (ADR-032). Extends
/// <see cref="IAuthUser"/> because the workflow verifies the current credential material before it
/// writes the new one.
/// </summary>
public interface IPasswordChangeableUser : IAuthUser
{
    /// <summary>
    /// Replaces the stored password material after the current password has been verified.
    /// </summary>
    /// <param name="newPasswordHash">The new PBKDF2 hash.</param>
    /// <param name="newPasswordSalt">The salt paired with <paramref name="newPasswordHash"/>.</param>
    /// <returns>A success result, or the aggregate's invariant failure.</returns>
    Result ChangePassword(byte[] newPasswordHash, byte[] newPasswordSalt);
}
