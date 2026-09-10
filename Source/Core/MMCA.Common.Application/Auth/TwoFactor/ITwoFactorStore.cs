using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.TwoFactor;

/// <summary>
/// Persistence for an account's two-factor state. The consumer implements it over its own
/// <c>User</c> aggregate (or a side table), which is why the framework ships no EF implementation
/// here the way it does for refresh sessions: the secret and the recovery hashes belong to the app's
/// user row, and the app's aggregate owns the invariants that guard them.
/// </summary>
/// <remarks>
/// <para>
/// Every mutating member returns a <see cref="Result"/> so an aggregate that refuses (an account
/// that is deactivated, one with no credential at all) can say so without an exception crossing the
/// layer, exactly like the rest of the Users use cases.
/// </para>
/// <para>
/// Implementations save their own work. The workflows call one member and treat its result as
/// final, so an implementation that stages a change without persisting it would report a
/// confirmation the user cannot rely on.
/// </para>
/// </remarks>
public interface ITwoFactorStore
{
    /// <summary>
    /// Reads the account's two-factor state, or <see langword="null"/> when the account does not
    /// exist. An account that never enrolled returns a state with
    /// <see cref="ITwoFactorUserState.IsTwoFactorEnabled"/> false, not null.
    /// </summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state, or <see langword="null"/>.</returns>
    Task<ITwoFactorUserState?> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a freshly minted secret WITHOUT activating the second factor, so an enrollment the user
    /// abandons half way cannot lock them out of their own account.
    /// </summary>
    /// <param name="userId">The enrolling account.</param>
    /// <param name="secret">The Base32 shared secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the aggregate's failure.</returns>
    Task<Result> StartEnrollmentAsync(UserIdentifierType userId, string secret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates the second factor and stores the account's first set of recovery-code hashes. Called
    /// only after a code minted from the stored secret has verified.
    /// </summary>
    /// <param name="userId">The enrolling account.</param>
    /// <param name="recoveryCodeHashes">The hashes to store; the plaintext never reaches this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the aggregate's failure.</returns>
    Task<Result> CompleteEnrollmentAsync(
        UserIdentifierType userId,
        IReadOnlyCollection<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the second factor off and clears the account's secret and recovery codes, so a later
    /// enrollment starts from a fresh secret rather than reviving an old one.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the aggregate's failure.</returns>
    Task<Result> DisableAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the whole recovery-code set. A replace, never an append: a regenerate exists to
    /// invalidate a list the user believes is compromised or lost.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="recoveryCodeHashes">The new hashes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the aggregate's failure.</returns>
    Task<Result> ReplaceRecoveryCodesAsync(
        UserIdentifierType userId,
        IReadOnlyCollection<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends one recovery code by removing its hash from the account.
    /// </summary>
    /// <remarks>
    /// SECURITY: this is what makes a recovery code single use, so it has to be persisted before the
    /// sign-in it authorized is answered. An implementation that removed the hash only in memory
    /// would let the same code sign in again.
    /// </remarks>
    /// <param name="userId">The account.</param>
    /// <param name="recoveryCodeHash">The hash that matched the presented code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the aggregate's failure.</returns>
    Task<Result> ConsumeRecoveryCodeAsync(
        UserIdentifierType userId,
        string recoveryCodeHash,
        CancellationToken cancellationToken = default);
}
