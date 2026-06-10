namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// Consumer-side idempotency store. Lets <c>IntegrationEventConsumer</c> skip an integration event
/// that has already been processed (at-least-once broker delivery can redeliver the same message).
/// The default implementation is a no-op; the EF-backed implementation is registered when the inbox
/// is enabled (<c>MessageBus:EnableInbox=true</c>).
/// </summary>
public interface IInboxStore
{
    /// <summary>Returns whether an event with the given <paramref name="messageId"/> has already been processed.</summary>
    Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Records that the event with the given <paramref name="messageId"/> has been processed.</summary>
    Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken);
}
