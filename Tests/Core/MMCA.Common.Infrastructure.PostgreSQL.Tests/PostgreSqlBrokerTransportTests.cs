using System.Security.Claims;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Shared.Auth;
using Npgsql;
using Testcontainers.PostgreSql;

// Aliased, not imported: MassTransit ships its own static MessageHeaders.
using MessageHeaders = MMCA.Common.Shared.Messaging.MessageHeaders;

namespace MMCA.Common.Infrastructure.PostgreSQL.Tests;

/// <summary>A probe event for the header round trip.</summary>
public sealed record class PgHeaderRoundTripEvent : BaseIntegrationEvent;

/// <summary>A probe event for the delayed-redelivery round trip.</summary>
public sealed record class PgRedeliveryEvent : BaseIntegrationEvent;

/// <summary>A probe event for the shared data-source (IAM token) path.</summary>
public sealed record class PgDataSourceEvent : BaseIntegrationEvent;

/// <summary>One PostgreSQL server for the broker tests; each test isolates itself by endpoint prefix.</summary>
public sealed class PostgreSqlBrokerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Gets the server's connection string, password included.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();
}

/// <summary>
/// The built-in PostgreSQL broker transport (<c>MessageBus:Provider=PostgreSql</c>), registered by
/// <c>AddBrokerMessaging</c> alone and started through its hosted services in registration order, the
/// way a generic host starts them: MassTransit's schema migration first, then the bus. Publishing goes
/// through <c>BrokerMessageBus</c>, the <c>IMessageBus</c> the outbox processor drains every integration
/// event through, and consuming through <c>IntegrationEventConsumer</c>.
/// <para>
/// The claims under test are the ones the unit tier cannot make: the transport schema really is created
/// before the bus needs it, the five ambient <c>MMCA-*</c> headers survive the SQL transport's message
/// rows, a failed consume comes back through the transport's native delayed delivery, and a registered
/// data-source configurator (the IAM token path) authenticates the broker's own connections.
/// </para>
/// </summary>
public sealed class PostgreSqlBrokerTransportTests(PostgreSqlBrokerFixture postgres) : IClassFixture<PostgreSqlBrokerFixture>
{
    private const string ExpectedTenantId = "tenant-pg";
    private const string ExpectedCorrelationId = "correlation-pg-1";
    private const string ExpectedUserId = "42";

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task PublishedEvent_CarriesEveryAmbientHeader_AndTheConsumerRestoresTheContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new HeaderProbe();
        var handler = new ContextRecordingHandler<PgHeaderRoundTripEvent>(failFirstAttempts: 0);

        await using var provider = BuildProvider(
            postgres.ConnectionString,
            "it-hdr-" + Guid.NewGuid().ToString("N")[..8],
            redeliveryIntervals: "0",
            services => services
                .AddSingleton(probe)
                .AddSingleton<IIntegrationEventHandler<PgHeaderRoundTripEvent>>(handler),
            bus =>
            {
                bus.RegisterIntegrationEventConsumer<PgHeaderRoundTripEvent>();
                bus.AddConsumer<HeaderProbeConsumer>();
            });

        await using var host = await StartedHost.StartAsync(provider, ct);

        var published = new PgHeaderRoundTripEvent();
        await CreatePublisher(provider.GetRequiredService<IBusControl>()).PublishAsync(published, ct);

        var headers = await probe.Received.Task.WaitAsync(DeliveryTimeout, ct);
        headers[MessageHeaders.TenantId].Should().Be(ExpectedTenantId);
        headers[MessageHeaders.UserId].Should().Be(ExpectedUserId);
        headers[MessageHeaders.UserRoles].Should().Be("Admin,Manager");
        headers[MessageHeaders.UserPermissions].Should().Be("[\"orders.read\",\"orders.write\"]");
        headers[MessageHeaders.CorrelationId].Should().Be(ExpectedCorrelationId);

        var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, ct);
        restored.MessageId.Should().Be(published.MessageId);
        restored.TenantId.Should().Be(ExpectedTenantId, "IntegrationEventConsumer restores the tenant before the handler runs");
        restored.CorrelationId.Should().Be(ExpectedCorrelationId);
    }

    [Fact]
    public async Task FailedConsume_IsRedeliveredThroughTheTransportsDelayedDelivery_WithTheHeadersIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ContextRecordingHandler<PgRedeliveryEvent>(failFirstAttempts: 1);

        await using var provider = BuildProvider(
            postgres.ConnectionString,
            "it-rdl-" + Guid.NewGuid().ToString("N")[..8],
            redeliveryIntervals: "2",
            services => services.AddSingleton<IIntegrationEventHandler<PgRedeliveryEvent>>(handler),
            bus => bus.RegisterIntegrationEventConsumer<PgRedeliveryEvent>());

        await using var host = await StartedHost.StartAsync(provider, ct);

        var published = new PgRedeliveryEvent();
        await CreatePublisher(provider.GetRequiredService<IBusControl>()).PublishAsync(published, ct);

        var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, ct);
        handler.Attempts.Should().Be(2, "in-process retry is off, so the second attempt is the second-level redelivery");
        restored.MessageId.Should().Be(published.MessageId);
        restored.TenantId.Should().Be(ExpectedTenantId, "the redelivered copy still carries the ambient headers");
        restored.CorrelationId.Should().Be(ExpectedCorrelationId);
    }

    [Fact]
    public async Task ARegisteredDataSourceConfigurator_AuthenticatesTheBrokersOwnConnections()
    {
        // The IAM-token path: the broker connection string carries NO password, and the server accepts
        // the bus's connections only because the shared data source asks the configurator's periodic
        // password provider. MassTransit's migrator cannot take that data source, so this host turns
        // the startup migration off and the schema is created first by a host that has the password,
        // which is the out-of-band step an IAM deployment runs.
        var ct = TestContext.Current.CancellationToken;
        await MigrateWithPasswordAsync(ct);

        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString);
        var configurator = new PeriodicPasswordConfigurator(builder.Password!);
        builder.Password = null;
        var handler = new ContextRecordingHandler<PgDataSourceEvent>(failFirstAttempts: 0);

        await using var provider = BuildProvider(
            builder.ConnectionString,
            "it-ds-" + Guid.NewGuid().ToString("N")[..8],
            redeliveryIntervals: "0",
            services => services
                .AddNpgsqlDataSourceConfigurator(configurator)
                .AddSingleton<IIntegrationEventHandler<PgDataSourceEvent>>(handler),
            bus => bus.RegisterIntegrationEventConsumer<PgDataSourceEvent>(),
            migrateOnStartup: false);

        await using var host = await StartedHost.StartAsync(provider, ct);

        var published = new PgDataSourceEvent();
        await CreatePublisher(provider.GetRequiredService<IBusControl>()).PublishAsync(published, ct);

        var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, ct);
        restored.MessageId.Should().Be(published.MessageId);
        configurator.PasswordRequests.Should().BePositive("the password reached the server only through the provider");
    }

    /// <summary>Runs only the startup migration, from a host whose connection string has the password.</summary>
    private async Task MigrateWithPasswordAsync(CancellationToken cancellationToken)
    {
        await using var migrator = BuildProvider(
            postgres.ConnectionString,
            "it-mig-" + Guid.NewGuid().ToString("N")[..8],
            redeliveryIntervals: "0",
            _ => { },
            _ => { });

        foreach (var migration in migrator.GetServices<IHostedService>().Where(s => s is MassTransit.SqlTransport.SqlTransportMigrationHostedService))
        {
            await migration.StartAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The bus a service host builds with <c>AddBrokerMessaging</c> alone: the PostgreSQL transport is
    /// built in, so there is no adapter call. The inbox is off (it needs the application's own tables)
    /// and in-process retries are off, so a failure goes straight to the delayed second level.
    /// </summary>
    private static ServiceProvider BuildProvider(
        string connectionString,
        string endpointPrefix,
        string redeliveryIntervals,
        Action<IServiceCollection> configureServices,
        Action<IBusRegistrationConfigurator> configureConsumers,
        bool migrateOnStartup = true)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "PostgreSql",
                ["MessageBus:ConnectionString"] = connectionString,
                ["MessageBus:EndpointPrefix"] = endpointPrefix,
                ["MessageBus:EnableInbox"] = "false",
                ["MessageBus:RetryLimit"] = "0",
                ["MessageBus:RedeliveryIntervalsSeconds:0"] = redeliveryIntervals,
                ["MessageBus:PostgreSql:MigrateOnStartup"] = migrateOnStartup ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection().AddLogging();

        // The two ambient carriers AddInfrastructure registers, so the consumer has something to
        // restore onto and a handler's own scope can read the restored values back.
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICorrelationContext, CorrelationContext>();

        // A generic host would let the bus start in the background; waiting makes the test publish
        // only once the endpoints are consuming.
        services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
        configureServices(services);

        services.AddBrokerMessaging(configuration, configureConsumers);

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

    /// <summary>
    /// Every registered hosted service, started in registration order and stopped in reverse, which is
    /// what the generic host does: this is what proves the migration runs before the bus.
    /// </summary>
    private sealed class StartedHost : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];

        private StartedHost()
        {
        }

        public static async Task<StartedHost> StartAsync(IServiceProvider provider, CancellationToken cancellationToken)
        {
            // Bounded: a bus that cannot authenticate retries its connection forever, so an unbounded
            // start would turn a broken data-source path into a hung run instead of a failed test.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(DeliveryTimeout);

            var host = new StartedHost();
            foreach (var service in provider.GetServices<IHostedService>())
            {
                await service.StartAsync(startup.Token);
                host._started.Add(service);
            }

            return host;
        }

        public async ValueTask DisposeAsync()
        {
            for (var i = _started.Count - 1; i >= 0; i--)
            {
                await _started[i].StopAsync(CancellationToken.None);
            }
        }
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
    }

    /// <summary>A plain consumer on its own queue, reading the headers before anything restores them.</summary>
    public sealed class HeaderProbeConsumer(HeaderProbe probe) : IConsumer<PgHeaderRoundTripEvent>
    {
        public Task Consume(ConsumeContext<PgHeaderRoundTripEvent> context)
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

            return Task.CompletedTask;
        }
    }

    /// <summary>What a handler saw through freshly constructed ambient carriers.</summary>
    public sealed record RestoredContext(Guid MessageId, string? TenantId, string? CorrelationId);

    /// <summary>
    /// A singleton handler that records the tenant and correlation id a NEW <c>TenantContext</c> /
    /// <c>CorrelationContext</c> sees, which is exactly what a scope opened from the root provider would
    /// construct: both seed themselves from the origin the consumer restored for this hop. Optionally
    /// throws on its first attempts to drive redelivery.
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

            var tenant = new TenantContext();
            var correlation = new CorrelationContext();
            Handled.TrySetResult(new RestoredContext(integrationEvent.MessageId, tenant.TenantId, correlation.CorrelationId));
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for an IAM token configurator: the password comes from a periodic provider.</summary>
    private sealed class PeriodicPasswordConfigurator(string password) : INpgsqlDataSourceConfigurator
    {
        private int _passwordRequests;

        public int PasswordRequests => Volatile.Read(ref _passwordRequests);

        public void Configure(NpgsqlDataSourceBuilder builder) =>
            builder.UsePeriodicPasswordProvider(
                (_, _) =>
                {
                    Interlocked.Increment(ref _passwordRequests);
                    return ValueTask.FromResult(password);
                },
                TimeSpan.FromMinutes(10),
                TimeSpan.FromSeconds(30));
    }
}
