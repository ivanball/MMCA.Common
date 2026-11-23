namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// A message a transport received on the host's queue, handed to <see cref="IBrokerReceivePipeline"/>.
/// The transport fills <see cref="Headers"/> from whatever the broker carried (string values only) and
/// leaves every decision to the pipeline.
/// </summary>
/// <param name="Body">The body bytes exactly as published.</param>
/// <param name="Headers">The headers exactly as published, string to string.</param>
/// <param name="BrokerMessageId">The broker's own id for the delivery, for logs and dead-letter diagnostics; may be empty.</param>
public sealed record IncomingBrokerMessage(
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> Headers,
    string BrokerMessageId)
{
    /// <summary>Gets the wire name from <see cref="BrokerHeaders.EventName"/>, or <see langword="null"/> when absent.</summary>
    public string? EventName => Headers.TryGetValue(BrokerHeaders.EventName, out var name) ? name : null;

    /// <summary>
    /// Gets the second-level redelivery count from <see cref="BrokerHeaders.RedeliveryCount"/>; absent or
    /// malformed reads as zero.
    /// </summary>
    public int RedeliveryCount =>
        Headers.TryGetValue(BrokerHeaders.RedeliveryCount, out var value)
        && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;
}
