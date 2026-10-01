using System.Collections.Concurrent;
using System.Diagnostics;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.LoadTests.Support;

/// <summary>The integration event the outbox scenario enqueues; <see cref="Sequence"/> makes each payload distinct.</summary>
/// <param name="Sequence">The enqueue order of this event.</param>
public sealed record LoadIntegrationEvent(int Sequence) : BaseIntegrationEvent;

/// <summary>
/// A transport that delivers nowhere and records everything: how many publishes arrived, how many
/// DISTINCT messages they carried (a duplicate dispatch would show as count above distinct), and the
/// timestamps of the first and last publish, which bound the dispatch window the throughput is
/// computed over. Pre-registered ahead of <c>AddInfrastructure</c>, so it replaces the default
/// <c>InProcessMessageBus</c>; the outbox processor resolves it per row exactly as it would a broker.
/// </summary>
internal sealed class CountingMessageBus(int expected) : IMessageBus
{
    private readonly ConcurrentDictionary<Guid, byte> _distinct = new();
    private readonly TaskCompletionSource _reachedExpected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _published;
    private long _firstTimestamp;
    private long _lastTimestamp;

    public long Published => Interlocked.Read(ref _published);

    public int Distinct => _distinct.Count;

    /// <summary>Gets the time between the first and the last publish.</summary>
    public TimeSpan DispatchWindow => Stopwatch.GetElapsedTime(
        Interlocked.Read(ref _firstTimestamp),
        Interlocked.Read(ref _lastTimestamp));

    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _firstTimestamp, now, 0);
        Interlocked.Exchange(ref _lastTimestamp, now);
        _distinct.TryAdd(integrationEvent.MessageId, 0);

        if (Interlocked.Increment(ref _published) >= expected)
        {
            _reachedExpected.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public async Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvents);

        foreach (var integrationEvent in integrationEvents)
        {
            await PublishAsync(integrationEvent, cancellationToken);
        }
    }

    /// <summary>Completes once the expected number of publishes has arrived, or throws on timeout.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    public Task WaitForExpectedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _reachedExpected.Task.WaitAsync(timeout, cancellationToken);
}
