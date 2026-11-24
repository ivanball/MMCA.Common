namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The SQS calls that settle one received message, behind an interface so the disposition logic in
/// <see cref="AmazonSqsDeliveryHandler"/> is unit-testable without a broker. The receiver's
/// implementation holds the message's receipt handle and the URLs of the host's queue and error queue.
/// </summary>
internal interface IAmazonSqsSettlement
{
    /// <summary>Deletes the received message from the host's queue.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when SQS deleted the message.</returns>
    Task DeleteAsync(CancellationToken cancellationToken);

    /// <summary>Sends a copy to the host's queue, visible after <paramref name="delaySeconds"/>.</summary>
    /// <param name="body">The body text.</param>
    /// <param name="headers">The copy's headers (packed into one attribute).</param>
    /// <param name="delaySeconds">The SQS delay, 0 to 900 seconds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when SQS accepted the copy.</returns>
    Task SendToQueueAsync(string body, IReadOnlyDictionary<string, string> headers, int delaySeconds, CancellationToken cancellationToken);

    /// <summary>Sends a copy to the host's <c>{queue}-error</c> queue.</summary>
    /// <param name="body">The body text.</param>
    /// <param name="headers">The copy's headers, including the dead-letter reason (packed into one attribute).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when SQS accepted the copy.</returns>
    Task SendToErrorQueueAsync(string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the received message's visibility timeout from now: a heartbeat while it is processed, or
    /// zero to hand it back to the queue at once.
    /// </summary>
    /// <param name="visibilityTimeoutSeconds">Seconds until the message becomes visible again.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when SQS changed the visibility.</returns>
    Task ChangeVisibilityAsync(int visibilityTimeoutSeconds, CancellationToken cancellationToken);
}
