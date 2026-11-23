using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Testing.Azure.Fixtures;

namespace MMCA.Common.Azure.ServiceBusEmulator.Tests;

/// <summary>Consumed and recorded: the plain round trip.</summary>
public sealed record class RoundTripEvent(Guid Marker, string Payload) : BaseIntegrationEvent;

/// <summary>Consumed by a handler that fails the FIRST delivery of each message, so it needs one redelivery.</summary>
public sealed record class FlakyEvent(Guid Marker) : BaseIntegrationEvent;

/// <summary>Consumed by a handler that always fails, so it ends in the dead-letter queue.</summary>
public sealed record class DoomedEvent(Guid Marker) : BaseIntegrationEvent;

/// <summary>Published only, never consumed: its topic exists only because the publisher created it.</summary>
public sealed record class PublishOnlyEvent(Guid Marker) : BaseIntegrationEvent;

/// <summary>
/// The tier's emulator plus ONE host running the framework's Azure Service Bus transport. The retry
/// policy is shrunk so a failure reaches the second level at once: no in-process retry, then a single
/// redelivery two seconds later, then the dead-letter queue.
/// </summary>
public sealed class ServiceBusEmulatorFixture : ServiceBusEmulatorFixtureBase
{
    /// <summary>The host's queue, also the application namespace its topics are prefixed with.</summary>
    public const string QueueName = "mmca-common-sb-emulator";

    /// <summary>The single redelivery interval, in seconds.</summary>
    public const int RedeliverySeconds = 2;

    /// <summary>Gets the delivery attempts the flaky and doomed handlers saw, per event marker.</summary>
    public ConcurrentDictionary<Guid, int> Attempts { get; } = new();

    /// <inheritdoc />
    protected override string? ReceiveQueueName => QueueName;

    /// <inheritdoc />
    protected override void ConfigureHost(IDictionary<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration["MessageBus:RetryLimit"] = "0";
        configuration["MessageBus:RetryMinIntervalSeconds"] = "0";
        configuration["MessageBus:RedeliveryIntervalsSeconds:0"] = RedeliverySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override void ConfigureConsumers(IBrokerConsumerBuilder consumers, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(services);

        RecordConsumed<RoundTripEvent>(consumers, services);

        consumers.RegisterIntegrationEventConsumer<FlakyEvent>();
        services.AddSingleton<IIntegrationEventHandler<FlakyEvent>>(new FlakyHandler(this));

        consumers.RegisterIntegrationEventConsumer<DoomedEvent>();
        services.AddSingleton<IIntegrationEventHandler<DoomedEvent>>(new DoomedHandler(this));
    }

    private int CountAttempt(Guid marker) => Attempts.AddOrUpdate(marker, 1, (_, count) => count + 1);

    private sealed class FlakyHandler(ServiceBusEmulatorFixture fixture) : IIntegrationEventHandler<FlakyEvent>
    {
        public Task HandleAsync(FlakyEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            if (fixture.CountAttempt(integrationEvent.Marker) == 1)
            {
                throw new InvalidOperationException("The first delivery fails on purpose.");
            }

            fixture.Consumed.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class DoomedHandler(ServiceBusEmulatorFixture fixture) : IIntegrationEventHandler<DoomedEvent>
    {
        public Task HandleAsync(DoomedEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            fixture.CountAttempt(integrationEvent.Marker);
            throw new InvalidOperationException("Every delivery fails on purpose.");
        }
    }
}

/// <summary>Shares the single warm emulator across every test class; tests run serially.</summary>
[CollectionDefinition(Name)]
public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
{
    /// <summary>The collection name referenced by <c>[Collection]</c>.</summary>
    public const string Name = "Azure Service Bus emulator";
}
