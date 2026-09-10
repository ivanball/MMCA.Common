using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The email-confirmation surface an Identity module's <c>User</c> aggregate exposes to the shared
/// <c>ConfirmEmailHandlerBase</c> workflow and to the sign-in gate.
/// </summary>
/// <remarks>
/// <para>
/// Opting in is what turns the feature on for an app: an aggregate that does not implement this
/// interface is never asked about confirmation, and
/// <c>EmailConfirmationSettings.RequireConfirmedEmail</c> has nothing to gate. Nothing else in the
/// framework changes shape.
/// </para>
/// <para>
/// <see cref="ConfirmEmail"/> returns a <see cref="Result"/> rather than throwing so the aggregate
/// keeps the last word: an app whose account states forbid confirming (an account already erased,
/// say) refuses here and the handler surfaces that failure unchanged.
/// </para>
/// </remarks>
public interface IEmailConfirmableUser
{
    /// <summary>Whether the account's email address has been proved.</summary>
    bool IsEmailConfirmed { get; }

    /// <summary>
    /// Marks the address confirmed after a single-use token has been redeemed for this account.
    /// Implementations should be idempotent: redeeming a second token for an address that is already
    /// confirmed is an ordinary duplicate, not a fault.
    /// </summary>
    /// <returns>A success result, or the aggregate's invariant failure.</returns>
    Result ConfirmEmail();
}
