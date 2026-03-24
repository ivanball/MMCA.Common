namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Validates whether a user account has been soft-deleted (BR-133).
/// Implemented by the Identity module to avoid cross-module domain references.
/// </summary>
public interface ISoftDeletedUserValidator
{
    /// <summary>
    /// Checks if the user with the given ID has been soft-deleted.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the user is soft-deleted; otherwise <see langword="false"/>.</returns>
    Task<bool> IsUserSoftDeletedAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);
}
