using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Infrastructure.Webhooks;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// The webhook delivery lane's registration and lane record (design-webhook-lane.md 1(d), rules L1.1,
/// L1.4, L1.5, L1.7, L1.18, L1.19, L1.20): <c>AddWebhooks</c> registers the lane registry and one lane
/// hosted service only when <c>Webhooks:Enabled</c> and <c>InternalCommands:Enabled</c> are both true,
/// once however often it is called; the lane claims the stored name of
/// <see cref="DeliverWebhookInternalCommand"/> and keys each row by its subscription.
/// </summary>
public sealed class WebhookDeliveryLaneTests
{
    private const string DeliverCommandName = "MMCA.Webhooks.Deliver.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddWebhooks_EnabledWithTheQueueOn_RegistersTheRegistryAndOneLaneService(bool queueFlagExplicit)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true" };
        if (queueFlagExplicit)
        {
            values["InternalCommands:Enabled"] = "true";
        }

        // Absent, InternalCommands:Enabled defaults to true (InternalCommandsSettings.cs:27).
        var services = CreateServices(values);
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(d => d.ServiceType == typeof(InternalCommandLanes))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        provider.GetRequiredService<InternalCommandLanes>().DedicatedCommandType.Should().Be(DeliverCommandName);
        CountLaneServices(services, provider).Should().Be(1);
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public void AddWebhooks_WebhooksDisabled_RegistersNoLane(string? webhooksEnabled)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal) { ["InternalCommands:Enabled"] = "true" };
        if (webhooksEnabled is not null)
        {
            values["Webhooks:Enabled"] = webhooksEnabled;
        }

        var services = CreateServices(values);
        using var provider = services.BuildServiceProvider();

        services.Should().NotContain(d => d.ServiceType == typeof(InternalCommandLanes));
        provider.GetService<InternalCommandLanes>().Should().BeNull("with no registry the shared loop excludes nothing (L1.20)");
        CountLaneServices(services, provider).Should().Be(0);
    }

    [Fact]
    public void AddWebhooks_QueueDisabled_RegistersNoLane()
    {
        var services = CreateServices(new(StringComparer.Ordinal)
        {
            ["Webhooks:Enabled"] = "true",
            ["InternalCommands:Enabled"] = "false",
        });
        using var provider = services.BuildServiceProvider();

        services.Should().NotContain(d => d.ServiceType == typeof(InternalCommandLanes), "a replica that only writes rows drains nothing");
        CountLaneServices(services, provider).Should().Be(0);
    }

    [Fact]
    public void AddWebhooks_Twice_RegistersOneLane()
    {
        var services = CreateServices(
            new(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true", ["InternalCommands:Enabled"] = "true" },
            addWebhooksCalls: 2);
        using var provider = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(InternalCommandLanes)).Should().Be(1, "the registry descriptor is the guard (L1.18)");
        CountLaneServices(services, provider).Should().Be(1);
    }

    [Fact]
    public void CommandType_IsTheStoredNameOfDeliverWebhookInternalCommand()
    {
        WebhookDeliveryLane.CommandType.Should().Be(
            InternalCommandNameResolver.GetStorageName(typeof(DeliverWebhookInternalCommand)),
            "the lane's command type is computed from the type, never a second copy of the literal (L1.4)");
        WebhookDeliveryLane.CommandType.Should().Be(DeliverCommandName);
        WebhookDeliveryLane.LaneName.Should().Be("webhooks");

        var lane = WebhookDeliveryLane.Create(new WebhookSettings());

        lane.Name.Should().Be(WebhookDeliveryLane.LaneName);
        lane.CommandType.Should().Be(WebhookDeliveryLane.CommandType);
    }

    [Fact]
    public void ReceiverKey_IsTheSubscriptionId()
    {
        var lane = WebhookDeliveryLane.Create(new WebhookSettings());
        var subscriptionId = Guid.NewGuid();

        var key = lane.ReceiverKey(NewDelivery(subscriptionId));

        key.Should().Be(subscriptionId.ToString("N"), "L1.7: the receiver key is the delivery's SubscriptionId formatted N");
        lane.ReceiverKey(NewDelivery(subscriptionId)).Should().Be(key, "two deliveries to one subscription share a receiver");
        lane.ReceiverKey(NewDelivery(Guid.NewGuid())).Should().NotBe(key);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(17, 17)]
    public void Create_TakesTheCooldownFromSettings(int? configuredSeconds, int expectedSeconds)
    {
        var settings = configuredSeconds is { } seconds
            ? new WebhookSettings { ReceiverCooldownSeconds = seconds }
            : new WebhookSettings();

        WebhookDeliveryLane.Create(settings).ReceiverCooldown.Should().Be(
            TimeSpan.FromSeconds(expectedSeconds), "the record carries the configured value; the clamp is applied by the lane (L1.19)");
    }

    // L1.5: with the framework InternalCommandSignal the lane has a wake of its own; with any other
    // IInternalCommandSignal it cannot, so it says so ONCE at start (Information) beside its own start
    // line (L1.17). The "polls" half (the lane then waits on its smart wait and polling interval only,
    // never on the custom signal, which would steal the shared loop's permit) is checked only as far
    // as start: the frozen clock keeps the loop in its startup delay.
    // DESIGN-QUESTION: observing the lane's first real wait deterministically needs a test hook on it.
    [Fact]
    public async Task LaneService_WithACustomSignal_LogsOnceAndPolls()
    {
        using var frameworkSignal = new InternalCommandSignal();
        var withFramework = await StartLaneAndCollectAsync(frameworkSignal, expectedEntries: 1);

        var custom = new Mock<IInternalCommandSignal>();
        var withCustom = await StartLaneAndCollectAsync(custom.Object, expectedEntries: 2);

        withFramework.Count(e => e.Level == LogLevel.Information).Should().Be(1, "the start line only");
        withCustom.Count(e => e.Level == LogLevel.Information).Should().Be(2, "the start line plus one custom-signal notice");
        withCustom.Should().NotContain(e => e.Level >= LogLevel.Warning);
        custom.Verify(s => s.WaitAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // L2.11 (lane Phase 2): the lane takes its concurrency from Webhooks:DeliveryConcurrency and its batch
    // start budget from Webhooks:TimeoutSeconds (design 2(a): BatchStartBudget = TimeoutSeconds).
    [Theory]
    [InlineData(null, null, 4, 10)]
    [InlineData(1, 30, 1, 30)]
    [InlineData(16, 1, 16, 1)]
    public void Create_TakesConcurrencyAndBudgetFromSettings(
        int? deliveryConcurrency,
        int? timeoutSeconds,
        int expectedConcurrency,
        int expectedBudgetSeconds)
    {
        var settings = deliveryConcurrency is { } concurrency && timeoutSeconds is { } timeout
            ? new WebhookSettings { DeliveryConcurrency = concurrency, TimeoutSeconds = timeout }
            : new WebhookSettings();

        var lane = WebhookDeliveryLane.Create(settings);

        lane.MaxConcurrency.Should().Be(expectedConcurrency);
        lane.BatchStartBudget.Should().Be(TimeSpan.FromSeconds(expectedBudgetSeconds));
    }

    // L2.11: the lane is built from the VALIDATED options, so a DeliveryConcurrency outside [1, 16] fails
    // when the registered lane is built, naming the key, instead of reaching the lane.
    [Fact]
    public void LaneService_IsBuiltFromTheValidatedOptions()
    {
        var services = CreateServices(new(StringComparer.Ordinal)
        {
            ["Webhooks:Enabled"] = "true",
            ["InternalCommands:Enabled"] = "true",
            ["Webhooks:DeliveryConcurrency"] = "17",
        });
        using var provider = services.BuildServiceProvider();

        var act = () => CountLaneServices(services, provider);

        act.Should().Throw<OptionsValidationException>().WithMessage("*DeliveryConcurrency*");
    }

    private static DeliverWebhookInternalCommand NewDelivery(Guid subscriptionId) =>
        new(subscriptionId, Guid.NewGuid(), "probe.happened", Guid.NewGuid(), DateTime.UtcNow, "{}");

    private static ServiceCollection CreateServices(Dictionary<string, string?> values, int addWebhooksCalls = 1)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();

        // What AddInfrastructure would have registered for the lane's shared processor instance.
        services.AddSingleton<IInternalCommandSignal, InternalCommandSignal>();
        services.AddSingleton(new FrameworkTableTargets(new EmptyEntityDataSourceRegistry(), new DefaultDataSourceResolver()));

        for (var call = 0; call < addWebhooksCalls; call++)
        {
            services.AddWebhooks(configuration);
        }

        return services;
    }

    /// <summary>
    /// Builds every factory-registered hosted service (1(d): the lane is a plain AddSingleton of
    /// IHostedService with a factory, because AddHostedService refuses a factory) and returns the lanes.
    /// </summary>
    private static List<InternalCommandLaneProcessor> ResolveLanes(ServiceCollection services, IServiceProvider provider)
    {
        var lanes = new List<InternalCommandLaneProcessor>();
        foreach (var descriptor in services.Where(d =>
            !d.IsKeyedService && d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null))
        {
            var instance = descriptor.ImplementationFactory!(provider);
            if (instance is InternalCommandLaneProcessor lane)
            {
                lanes.Add(lane);
            }
            else if (instance is IDisposable other)
            {
                other.Dispose();
            }
        }

        return lanes;
    }

    private static int CountLaneServices(ServiceCollection services, IServiceProvider provider)
    {
        var lanes = ResolveLanes(services, provider);
        foreach (var lane in lanes)
        {
            lane.Dispose();
        }

        return lanes.Count;
    }

    /// <summary>
    /// Starts the registered lane with <paramref name="signal"/> on a frozen fake clock (so it never
    /// leaves its startup delay), waits for its start lines, stops it, and returns what it logged.
    /// </summary>
    private static async Task<IReadOnlyList<StructuredRecordingLogger<InternalCommandLaneProcessor>.Entry>> StartLaneAndCollectAsync(
        IInternalCommandSignal signal,
        int expectedEntries)
    {
        var logger = new StructuredRecordingLogger<InternalCommandLaneProcessor>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(signal);
        services.AddSingleton<ILogger<InternalCommandLaneProcessor>>(logger);
        services.AddSingleton(new FrameworkTableTargets(new EmptyEntityDataSourceRegistry(), new DefaultDataSourceResolver()));
        services.AddWebhooks(configuration);
        await using var provider = services.BuildServiceProvider();

        var lane = ResolveLanes(services, provider).Should().ContainSingle().Subject;
        try
        {
            await lane.StartAsync(Ct);
            await logger.WhenCountAsync(expectedEntries).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
            await lane.StopAsync(Ct);
        }
        finally
        {
            lane.Dispose();
        }

        return logger.Entries;
    }
}
