using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// Persistence for <see cref="PasskeyCredential"/> rows (ADR-139). The framework ships the EF
/// implementation over the Identity database; <c>AddPasskeys</c> registers it with <c>TryAdd</c>, so a
/// host may supply its own.
/// </summary>
public interface IPasskeyCredentialStore
{
    /// <summary>Lists the account's passkeys, oldest first.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account's passkeys.</returns>
    Task<IReadOnlyList<PasskeyCredential>> ListForUserAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>Finds the account's passkey with this row id, or <see langword="null"/>.</summary>
    /// <param name="userId">The account; another account's row reads as missing.</param>
    /// <param name="id">The row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The passkey, or <see langword="null"/>.</returns>
    Task<PasskeyCredential?> FindAsync(UserIdentifierType userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finds the passkey with this WebAuthn credential id, across all accounts, or <see langword="null"/>.</summary>
    /// <param name="credentialId">The credential id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The passkey, or <see langword="null"/>.</returns>
    Task<PasskeyCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default);

    /// <summary>Adds a passkey and saves.</summary>
    /// <param name="credential">The new passkey.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Saves a change made to a passkey this store returned (a rename, a recorded use).</summary>
    /// <param name="credential">The changed passkey.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task UpdateAsync(PasskeyCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Deletes a passkey this store returned and saves.</summary>
    /// <param name="credential">The passkey to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveAsync(PasskeyCredential credential, CancellationToken cancellationToken = default);
}
