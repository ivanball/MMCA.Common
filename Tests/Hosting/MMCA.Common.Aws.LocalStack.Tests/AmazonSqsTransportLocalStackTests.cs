using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Shared.Auth;

// Aliased, not imported: MassTransit ships its own static MessageHeaders.
using MessageHeaders = MMCA.Common.Shared.Messaging.MessageHeaders;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>A probe event for the header round trip.</summary>
public sealed record class AwsHeaderRoundTripEvent : BaseIntegrationEvent;

/// <summary>A probe event for the delayed-redelivery round trip.</summary>
public sealed record class AwsRedeliveryEvent : BaseIntegrationEvent;

/// <summary>
/// The shipped SQS/SNS transport, registered through <c>AddAmazonSqsTransport()</c> +
/// <c>AddBrokerMessaging</c> exactly as a host would, against LocalStack. Publishing goes through
/// <c>BrokerMessageBus</c>, the <c>IMessageBus</c> the outbox processor drains every integration
/// event through, and consuming through <c>IntegrationEventConsumer</c>, the consumer every
/// integration event binds.
/// <para>
/// The claim under test is the one the unit tier cannot make: the five ambient <c>MMCA-*</c> headers
/// cross SNS and SQS intact and are restored on the far side. The transport keeps them out of the
/// SQS/SNS message attributes (10 at most per message), so they can only arrive inside the
/// MassTransit envelope body; this is where that is verified rather than assumed.
/// </para>
/// </summary>
public sealed class AmazonSqsTransportLocalStackTests(LocalStackFixture localStack)
{
    private const string ExpectedTenantId = "tenant-aws";
    private const string ExpectedCorrelationId = "correlation-aws-1";
    private const string ExpectedUserId = "42";

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task PublishedEvent_CarriesEveryAmbientHeader_AndTheConsumerRestoresTheContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new HeaderProbe();
        var handler = new ContextRecordingHandler<AwsHeaderRoundTripEvent>(failFirstAttempts: 0);

        await using var provider = BuildProvider(
            "it-hdr-" + Guid.NewGuid().ToString("N")[..8],
            redeliveryIntervals: ["0"],
            services => services
                .AddSingleton(probe)
                .AddSingleton<IIntegrationEventHandler<AwsHeaderRoundTripEvent>>(handler),
            bus =>
            {
                bus.RegisterIntegrationEventConsumer<AwsHeaderRoundTripEvent>();
                bus.AddConsumer<HeaderProbeConsumer>();
            });

        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync(ct);
        try
        {
            var published = new AwsHeaderRoundTripEvent();
            await CreatePublisher(busControl).PublishAsync(published, ct);

            var headers = await probe.Received.Task.WaitAsync(DeliveryTimeout, ct);
            headers[MessageHeaders.TenantId].Should().Be(ExpectedTenantId);
            headers[MessageHeaders.UserId].Should().Be(ExpectedUserId);
            headers[MessageHeaders.UserRoles].Should().Be("Admin,Manager");
            headers[MessageHeaders.UserPermissions].Should().Be("[\"orders.read\",\"orders.write\"]");
            headers[MessageHeaders.CorrelationId].Should().Be(ExpectedCorrelationId);

            // ...and they did NOT ride as SQS message attributes: the envelope body carried them,
            // leaving the 10-attribute budget to MassTransit's own transport headers.
            var attributeKeys = await probe.AttributeKeys.Task.WaitAsync(DeliveryTimeout, ct);
            attributeKeys.Should().NotBeEmpty("the probe must have read a real SQS message");
            attributeKeys.Should().NotContain(key => key.StartsWith("MMCA-", StringComparison.OrdinalIgnoreCase));
            attributeKeys.Count.Should().BeLessThanOrEqualTo(10);

            var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, ct);
            restored.MessageId.Should().Be(published.MessageId);
            restored.TenantId.Should().Be(ExpectedTenantId, "IntegrationEventConsumer restores the tenant before the handler runs");
            restored.CorrelationId.Should().Be(ExpectedCorrelationId);
        }
        finally
        {
            await busControl.StopAsync(ct);
        }
    }

    [Fact]
    public async Task FailedConsume_IsRedeliveredThroughAnSqsDelay_WithTheHeadersIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ContextRecordingHandler<AwsRedeliveryEvent>(failFirstAttempts: 1);

        await using var provider = BuildProvider(
            "it-rdl-" + Guid.NewGuid().ToString("N")[..8],
            redeliveryIntervals: ["2"],
            services => services.AddSingleton<IIntegrationEventHandler<AwsRedeliveryEvent>>(handler),
            bus => bus.RegisterIntegrationEventConsumer<AwsRedeliveryEvent>());

        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync(ct);
        try
        {
            var published = new AwsRedeliveryEvent();
            await CreatePublisher(busControl).PublishAsync(published, ct);

            var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, ct);
            handler.Attempts.Should().Be(2, "the first attempt throws and the second-level redelivery succeeds");
            restored.MessageId.Should().Be(published.MessageId);
            restored.TenantId.Should().Be(ExpectedTenantId, "the redelivered copy still carries the ambient headers");
            restored.CorrelationId.Should().Be(ExpectedCorrelationId);
        }
        finally
        {
            await busControl.StopAsync(ct);
        }
    }

    /// <summary>
    /// The bus a service host builds: the adapter transport, then <c>AddBrokerMessaging</c>. The
    /// inbox is off (it needs a database this tier does not have) and in-process retries are off,
    /// so a failure goes straight to the SQS-delayed second level.
    /// </summary>
    private ServiceProvider BuildProvider(
        string endpointPrefix,
        string[] redeliveryIntervals,
        Action<IServiceCollection> configureServices,
        Action<IBusRegistrationConfigurator> configureConsumers)
    {
        var settings = localStack.MessageBusSettings(endpointPrefix);
        settings["MessageBus:EnableInbox"] = "false";
        settings["MessageBus:RetryLimit"] = "0";
        for (var i = 0; i < redeliveryIntervals.Length; i++)
        {
            settings[string.Create(CultureInfo.InvariantCulture, $"MessageBus:RedeliveryIntervalsSeconds:{i}")] = redeliveryIntervals[i];
        }

        var services = new ServiceCollection().AddLogging();

        // The two ambient carriers AddInfrastructure registers, so the consumer has something to
        // restore onto and a handler's own scope can read the restored values back.
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICorrelationContext, CorrelationContext>();
        configureServices(services);

        services.AddAmazonSqsTransport();
        services.AddBrokerMessaging(LocalStackFixture.ConfigurationWith(settings), configureConsumers);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// <c>BrokerMessageBus</c> carrying the ambient context of an authenticated request, the state
    /// the outbox processor restores onto its scope before it publishes a row.
    /// </summary>
    private static BrokerMessageBus CreatePublisher(IPublishEndpoint publishEndpoint)
    {
        var tenant = new TenantContext();
        tenant.SetTenant(ExpectedTenantId);
        var correlation = new CorrelationContext();
        correlation.SetCorrelationId(ExpectedCorrelationId);

        return new BrokerMessageBus(publishEndpoint, new StaticCurrentUser(), tenant, correlation);
    }

    /// <summary>An authenticated user with two roles and two permission claims.</summary>
    private sealed class StaticCurrentUser : ICurrentUserService
    {
        public ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, ExpectedUserId),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim(ClaimTypes.Role, "Manager"),
                new Claim(AuthClaimTypes.Permission, "orders.read"),
                new Claim(AuthClaimTypes.Permission, "orders.write"),
            ],
            authenticationType: "Test"));

        public UserIdentifierType? UserId => 42;

        public string? Role => "Admin";

        public T? GetClaimValue<T>(string claimType)
            where T : struct, IParsable<T> => null;
    }

    /// <summary>Collects the ambient headers exactly as the consume context exposes them.</summary>
    public sealed class HeaderProbe
    {
        public TaskCompletionSource<IReadOnlyDictionary<string, string?>> Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyCollection<string>> AttributeKeys { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A plain consumer on its own queue, reading the headers before anything restores them.</summary>
    public sealed class HeaderProbeConsumer(HeaderProbe probe) : IConsumer<AwsHeaderRoundTripEvent>
    {
        public Task Consume(ConsumeContext<AwsHeaderRoundTripEvent> context)
        {
            ArgumentNullException.ThrowIfNull(context);

            probe.Received.TrySetResult(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [MessageHeaders.TenantId] = context.Headers.Get<string>(MessageHeaders.TenantId, null),
                [MessageHeaders.UserId] = context.Headers.Get<string>(MessageHeaders.UserId, null),
                [MessageHeaders.UserRoles] = context.Headers.Get<string>(MessageHeaders.UserRoles, null),
                [MessageHeaders.UserPermissions] = context.Headers.Get<string>(MessageHeaders.UserPermissions, null),
                [MessageHeaders.CorrelationId] = context.Headers.Get<string>(MessageHeaders.CorrelationId, null),
            });

            // The raw SQS message attributes, as the transport received them.
            probe.AttributeKeys.TrySetResult(
                context.TryGetPayload<AmazonSqsMessageContext>(out var sqsContext)
                    ? [.. sqsContext.Attributes.Keys]
                    : []);

            return Task.CompletedTask;
        }
    }

    /// <summary>What a handler saw through freshly constructed ambient carriers.</summary>
    public sealed record RestoredContext(Guid MessageId, string? TenantId, string? CorrelationId);

    /// <summary>
    /// A singleton handler that records the tenant and correlation id a NEW <c>TenantContext</c> /
    /// <c>CorrelationContext</c> sees, which is exactly what a scope opened from the root provider
    /// (the <c>ScopedIntegrationEventHandlerBase</c> pattern) would construct: both seed themselves
    /// from the origin the consumer restored for this hop. Optionally throws on its first attempts to
    /// drive redelivery.
    /// </summary>
    /// <typeparam name="TEvent">The integration event handled.</typeparam>
    public sealed class ContextRecordingHandler<TEvent>(int failFirstAttempts) : IIntegrationEventHandler<TEvent>
        where TEvent : BaseIntegrationEvent
    {
        private int _attempts;

        public TaskCompletionSource<RestoredContext> Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts => Volatile.Read(ref _attempts);

        public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(integrationEvent);

            if (Interlocked.Increment(ref _attempts) <= failFirstAttempts)
            {
                throw new InvalidOperationException("Simulated handler failure.");
            }

            // A fresh carrier seeds itself from the origin the consumer published for this hop.
            var tenant = new TenantContext();
            var correlation = new CorrelationContext();
            Handled.TrySetResult(new RestoredContext(integrationEvent.MessageId, tenant.TenantId, correlation.CorrelationId));
            return Task.CompletedTask;
        }
    }
}
