using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// The hosted receiver's processor options (manual settlement, the backpressure knobs and a lock
/// renewal that outlasts the in-process retry budget) and the publish-only host that runs no
/// processor at all.
/// </summary>
public sealed class AzureServiceBusReceiverTests
{
    private const string ConnectionString =
        "Endpoint=sb://mmca-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=cmVkYWN0ZWQ=";

    [Fact]
    public void ProcessorOptions_SettleManually_InPeekLock()
    {
        var options = AzureServiceBusReceiver.BuildProcessorOptions(new MessageBusSettings());

        options.AutoCompleteMessages.Should().BeFalse("the transport settles each message from the pipeline's disposition");
        options.ReceiveMode.Should().Be(ServiceBusReceiveMode.PeekLock);
    }

    [Fact]
    public void ProcessorOptions_Unset_UseTheTransportDefaults()
    {
        var options = AzureServiceBusReceiver.BuildProcessorOptions(new MessageBusSettings());

        options.MaxConcurrentCalls.Should().Be(AzureServiceBusReceiver.DefaultMaxConcurrentCalls);
        options.PrefetchCount.Should().Be(AzureServiceBusReceiver.DefaultPrefetchCount);
    }

    [Fact]
    public void ProcessorOptions_TakeTheConfiguredBackpressure()
    {
        var options = AzureServiceBusReceiver.BuildProcessorOptions(new MessageBusSettings { PrefetchCount = 40, ConcurrentMessageLimit = 8 });

        options.PrefetchCount.Should().Be(40);
        options.MaxConcurrentCalls.Should().Be(8);
    }

    [Fact]
    public void ProcessorOptions_NonPositiveBackpressure_FallsBackToTheDefaults()
    {
        var options = AzureServiceBusReceiver.BuildProcessorOptions(new MessageBusSettings { PrefetchCount = 0, ConcurrentMessageLimit = -1 });

        options.MaxConcurrentCalls.Should().Be(AzureServiceBusReceiver.DefaultMaxConcurrentCalls);
        options.PrefetchCount.Should().Be(AzureServiceBusReceiver.DefaultPrefetchCount);
    }

    [Fact]
    public void ProcessorOptions_RenewTheLockFarBeyondTheRetryBudget() =>
        AzureServiceBusReceiver.BuildProcessorOptions(new MessageBusSettings())
            .MaxAutoLockRenewalDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(10));

    [Fact]
    public async Task AHostWithNoSubscriptions_StartsAndStopsWithoutTouchingTheBroker()
    {
        // The namespace does not exist: provisioning or a processor would fail or hang, so a clean,
        // prompt start and stop proves neither ran.
        var connection = new AzureServiceBusConnection(
            new AzureServiceBusEndpoint(null, null, ConnectionString, ConnectionString, isEmulator: false));
        await using var _ = connection;
        var receiver = new AzureServiceBusReceiver(
            connection,
            new BrokerTopology("demo", "orders", []),
            new AzureServiceBusDeliveryHandler(new NeverCalledPipeline(), TimeProvider.System, NullLogger.Instance),
            new AzureServiceBusTopologyProvisioner(connection.Administration, isEmulator: false, NullLogger.Instance),
            new MessageBusSettings(),
            NullLogger.Instance);

        await receiver.StartAsync(TestContext.Current.CancellationToken);
        await receiver.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await receiver.StopAsync(TestContext.Current.CancellationToken);

        receiver.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        receiver.Dispose();
    }

    private sealed class NeverCalledPipeline : IBrokerReceivePipeline
    {
        public Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No message may be processed.");
    }
}
