using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Users;

/// <summary>
/// The self-service authorization rule shared by every use case that acts on one account on behalf
/// of its owner: the caller must be the account owner, or hold the app's privileged role.
/// </summary>
/// <remarks>
/// The idiom (<c>CurrentUserId != UserId &amp;&amp; !role-bypass -&gt; Error.Forbidden</c>) was written
/// out four times across the two apps (account deletion and data export, in each). It is hoisted as a
/// plain helper rather than folded into a base class because the two data-export handlers stay
/// app-level (their projections are entirely app-specific) and still need the identical decision and
/// the identical error shape.
/// <para>
/// The role test is passed in already evaluated: each app owns its own role vocabulary
/// (<c>UserRole.IsOrganizer</c> vs <c>UserRole.IsAdmin</c>), and both are case-insensitive because a
/// role claim may carry any casing.
/// </para>
/// </remarks>
public static class UserOwnershipRule
{
    /// <summary>
    /// Evaluates the owner-or-privileged-role rule.
    /// </summary>
    /// <param name="request">The user-scoped request carrying the target and the caller.</param>
    /// <param name="callerHasPrivilegedRole">
    /// Whether the caller's role bypasses the ownership requirement (evaluated by the app, e.g.
    /// <c>UserRole.IsOrganizer(request.CurrentUserRole)</c>).
    /// </param>
    /// <param name="code">The error code to report when the rule rejects (e.g. "User.DeleteForbidden").</param>
    /// <param name="message">The message to report when the rule rejects.</param>
    /// <param name="source">The reporting handler name.</param>
    /// <returns>
    /// <see langword="null"/> when the caller is allowed; otherwise the <see cref="ErrorType.Forbidden"/>
    /// error the caller should be failed with.
    /// </returns>
    public static Error? CheckOwnership(
        IUserOwnedRequest request,
        bool callerHasPrivilegedRole,
        string code,
        string message,
        string source)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.CurrentUserId == request.UserId || callerHasPrivilegedRole
            ? null
            : Error.Forbidden(
                code: code,
                message: message,
                source: source,
                target: nameof(IUserOwnedRequest.UserId));
    }
}
