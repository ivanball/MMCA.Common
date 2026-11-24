using Azure.Messaging.ServiceBus;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Service Bus calls that settle one delivered message, behind an interface so the disposition
/// logic in <see cref="AzureServiceBusDeliveryHandler"/> is unit-testable without a broker. The
/// receiver's implementation wraps the processor's event arguments and the queue's sender.
/// </summary>
internal interface IAzureServiceBusSettlement
{
    /// <summary>Completes (removes) the delivered message.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker settled the message.</returns>
    Task CompleteAsync(CancellationToken cancellationToken);

    /// <summary>Moves the delivered message to the queue's native dead-letter queue.</summary>
    /// <param name="reason">The dead-letter reason.</param>
    /// <param name="description">The dead-letter error description, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker settled the message.</returns>
    Task DeadLetterAsync(string reason, string? description, CancellationToken cancellationToken);

    /// <summary>Releases the delivered message's lock so the broker delivers it again.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker released the lock.</returns>
    Task AbandonAsync(CancellationToken cancellationToken);

    /// <summary>Schedules <paramref name="message"/> on the host's queue.</summary>
    /// <param name="message">The message to schedule.</param>
    /// <param name="enqueueTime">When the message becomes visible.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker accepted the scheduled message.</returns>
    Task ScheduleOnQueueAsync(ServiceBusMessage message, DateTimeOffset enqueueTime, CancellationToken cancellationToken);
}
