namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Default no-op recipient provider that returns an empty list.
/// Consuming apps should register their own <see cref="INotificationRecipientProvider"/>
/// to supply actual recipients.
/// </summary>
public sealed class NullNotificationRecipientProvider : INotificationRecipientProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyList<UserIdentifierType>> GetRecipientUserIdsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserIdentifierType>>([]);
}
