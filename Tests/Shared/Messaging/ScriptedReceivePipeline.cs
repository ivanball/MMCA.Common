using System.Collections.Concurrent;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Tests.Shared.Messaging;

/// <summary>
/// An <see cref="IBrokerReceivePipeline"/> stand-in for the transport tiers: records every delivery with
/// the time it arrived and answers with the disposition a script picks, so a test drives the
/// transport's disposition handling against a real broker without any consumer, inbox or database.
/// </summary>
/// <param name="script">Picks the disposition from the delivery and its 1-based number for the same message id.</param>
public sealed class ScriptedReceivePipeline(Func<IncomingBrokerMessage, int, BrokerDisposition>? script = null) : IBrokerReceivePipeline
{
    private readonly ConcurrentQueue<Delivery> _deliveries = new();
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Gets every delivery, in arrival order.</summary>
    public IReadOnlyList<Delivery> Deliveries => [.. _deliveries];

    /// <inheritdoc />
    public Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        // Copy the body: a transport may reuse its buffer once the call returns.
        var copy = message with { Body = message.Body.ToArray() };
        var key = message.Headers.TryGetValue(BrokerHeaders.MessageId, out var id) ? id : message.BrokerMessageId;
        var attempt = _attempts.AddOrUpdate(key, 1, (_, previous) => previous + 1);
        _deliveries.Enqueue(new Delivery(copy, DateTimeOffset.UtcNow));

        return Task.FromResult(script?.Invoke(copy, attempt) ?? BrokerDisposition.Complete);
    }

    /// <summary>Waits until at least <paramref name="count"/> deliveries have arrived.</summary>
    /// <param name="count">The delivery count to wait for.</param>
    /// <param name="timeout">How long to wait before failing.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The deliveries so far.</returns>
    /// <exception cref="TimeoutException">Fewer deliveries arrived within <paramref name="timeout"/>.</exception>
    public async Task<IReadOnlyList<Delivery>> WaitForAsync(int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_deliveries.Count < count)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Expected {count} deliveries within {timeout}, got {_deliveries.Count}."));
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        return Deliveries;
    }

    /// <summary>One delivery to the pipeline.</summary>
    /// <param name="Message">The delivered message (body copied).</param>
    /// <param name="ReceivedAt">When it arrived.</param>
    public sealed record Delivery(IncomingBrokerMessage Message, DateTimeOffset ReceivedAt);
}
