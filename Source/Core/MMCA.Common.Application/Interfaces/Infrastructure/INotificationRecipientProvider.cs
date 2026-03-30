namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Provides the set of user IDs that should receive a push notification.
/// Consuming apps implement this to resolve recipients based on their domain model
/// (e.g., all attendees, users in a role, subscribers to a topic).
/// </summary>
public interface INotificationRecipientProvider
{
    /// <summary>
    /// Gets the user identifiers of all eligible notification recipients.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only list of recipient user identifiers.</returns>
    Task<IReadOnlyList<UserIdentifierType>> GetRecipientUserIdsAsync(
        CancellationToken cancellationToken = default);
}
