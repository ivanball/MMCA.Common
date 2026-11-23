namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// What a transport must do with a delivered message, as decided by <see cref="IBrokerReceivePipeline"/>.
/// A transport applies it and decides nothing itself: retry counts, intervals and dead-letter policy live
/// once, in the pipeline.
/// </summary>
public abstract record BrokerDisposition
{
    /// <summary>Gets the shared <see cref="Completed"/> instance.</summary>
    public static BrokerDisposition Complete { get; } = new Completed();

    /// <summary>The message was handled (or deliberately skipped as a duplicate): acknowledge and remove it.</summary>
    public sealed record Completed : BrokerDisposition;

    /// <summary>
    /// Handling failed and a second-level redelivery is due: enqueue a copy of the message on the host's
    /// queue to become visible after <paramref name="Delay"/>, with <see cref="BrokerHeaders.RedeliveryCount"/>
    /// set to <paramref name="RedeliveryCount"/> and every other header and the body unchanged, then
    /// acknowledge the original.
    /// </summary>
    /// <param name="Delay">How long until the copy may be delivered.</param>
    /// <param name="RedeliveryCount">The value the copy's redelivery-count header carries.</param>
    public sealed record Redeliver(TimeSpan Delay, int RedeliveryCount) : BrokerDisposition;

    /// <summary>
    /// The message cannot be handled (poison, unknown event, or every retry and redelivery failed): move it
    /// to the queue's dead-letter location with <paramref name="Reason"/> and <paramref name="Detail"/>
    /// attached where the broker allows, then acknowledge it.
    /// </summary>
    /// <param name="Reason">A short machine-friendly reason, e.g. <c>handler-failed</c>, <c>unknown-event</c>, <c>poison</c>.</param>
    /// <param name="Detail">A human-readable description (exception messages), or <see langword="null"/>.</param>
    public sealed record DeadLetter(string Reason, string? Detail) : BrokerDisposition;
}
