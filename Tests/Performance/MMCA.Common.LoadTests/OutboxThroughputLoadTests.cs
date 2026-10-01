using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.LoadTests.Support;

namespace MMCA.Common.LoadTests;

/// <summary>
/// Outbox throughput at volume: 10,000 integration events are enqueued the way the framework writes
/// them (<see cref="OutboxMessage.FromDomainEvent"/> rows saved through the scope's context), then the
/// REAL <see cref="OutboxProcessor"/>, resolved from <c>AddInfrastructure</c> as a hosted service and
/// started with its default settings (batch size, lease, retry), drains them to a counting transport.
/// Every message must be delivered exactly once and stamped processed, and the dispatch rate must stay
/// above a floor.
/// </summary>
public sealed class OutboxThroughputLoadTests
{
    private const int MessageCount = 10_000;

    // Enqueue in chunks, as many requests would, rather than one 10,000-row SaveChanges.
    private const int EnqueueChunkSize = 500;

    // Floor on messages dispatched per second over the dispatch window (first publish to last).
    // Measured locally (i9-14900K, Windows, Release build, 2026-10-01): 1,792 / 1,775 / 1,783 msg/s on
    // the three reference runs, worst 1,564 across 25 runs. Floor = about 1/3 of the worst run.
    private const double MinMessagesPerSecond = 500;

    // Generous bound on the whole drain, so a stalled processor fails the run instead of hanging it.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task OutboxProcessor_Drains10000Messages_ExactlyOnce_AboveThroughputFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new CountingMessageBus(MessageCount);
        await using var stack = await LoadStack.CreateAsync(
            "outbox",
            services => services.AddSingleton<IMessageBus>(bus),
            ct);

        // Arrange: enqueue. OccurredOn is a minute old, so every row is past the default 5s processing
        // delay and eligible on the first poll.
        var occurredOn = DateTime.UtcNow.AddMinutes(-1);
        var enqueue = System.Diagnostics.Stopwatch.StartNew();
        for (var start = 0; start < MessageCount; start += EnqueueChunkSize)
        {
            await using var scope = stack.Services.CreateAsyncScope();
            var context = LoadStack.GetContext(scope.ServiceProvider);
            var rows = Enumerable.Range(start, EnqueueChunkSize)
                .Select(i => OutboxMessage.FromDomainEvent(new LoadIntegrationEvent(i) { DateOccurred = occurredOn.AddTicks(i) }))
                .ToList();
            context.Set<OutboxMessage>().AddRange(rows);
            await context.SaveChangesAsync(ct);
        }

        enqueue.Stop();

        // Act: run the hosted processor until the transport has seen every message.
        var processor = stack.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        var drain = System.Diagnostics.Stopwatch.StartNew();
        await processor.StartAsync(ct);
        try
        {
            await bus.WaitForExpectedAsync(DrainTimeout, ct);

            // The last batch's ProcessedOn stamps are saved after its publishes: wait for that save
            // before stopping, or the stop could cancel it mid-flight.
            await WaitUntilAllProcessedAsync(stack, ct);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        drain.Stop();

        var window = bus.DispatchWindow;
        var messagesPerSecond = MessageCount / Math.Max(window.TotalSeconds, 0.001);

        await LoadResults.WriteAsync(
            "outbox-throughput",
            new
            {
                scenario = "outbox-throughput",
                messages = MessageCount,
                enqueueSeconds = LoadResults.Round(enqueue.Elapsed.TotalSeconds),
                dispatchWindowSeconds = LoadResults.Round(window.TotalSeconds),
                drainWallSecondsIncludingStartupDelay = LoadResults.Round(drain.Elapsed.TotalSeconds),
                messagesPerSecond = LoadResults.Round(messagesPerSecond),
                floorMessagesPerSecond = MinMessagesPerSecond,
                published = bus.Published,
                distinct = bus.Distinct,
            },
            ct);

        // Assert: every message delivered exactly once and persisted as processed.
        bus.Published.Should().Be(MessageCount, "each outbox row is published once by a single processor");
        bus.Distinct.Should().Be(MessageCount, "no message may be lost or delivered twice");
        messagesPerSecond.Should().BeGreaterThanOrEqualTo(
            MinMessagesPerSecond,
            "the outbox must sustain the dispatch-rate floor");
    }

    private static async Task WaitUntilAllProcessedAsync(LoadStack stack, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var scope = stack.Services.CreateAsyncScope();
            var pending = await LoadStack.GetContext(scope.ServiceProvider)
                .Set<OutboxMessage>()
                .CountAsync(m => m.ProcessedOn == null, ct);

            if (pending == 0)
            {
                return;
            }

            DateTime.UtcNow.Should().BeBefore(deadline, "every outbox row must be stamped processed once its publish arrived");
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }
}
