using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auditing;
using MMCA.Common.Shared.Resilience;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auditing;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Offline;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the resilience-relevant configuration of the named <c>"APIClient"</c> registered by
/// <c>AddUIShared</c>: its total timeout must come from the shared budget rather than
/// <see cref="HttpClient"/>'s uncoordinated 100s default, which would otherwise cut a call off at
/// an arbitrary point inside the retry pipeline.
/// </summary>
public sealed class ApiClientRegistrationTests
{
    private static ServiceProvider BuildProvider(string? endpoint = "https://localhost:6001")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:ApiEndpoint"] = endpoint })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // The APIClient pipeline resolves AuthDelegatingHandler, which needs token storage.
        services.AddSingleton<ITokenStorageService>(new StubTokenStorageService());
        services.AddUIShared(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void ApiClient_Timeout_MatchesTheSharedTotalRequestBudget()
    {
        using var provider = BuildProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("APIClient");

        client.Timeout.Should().Be(TimeSpan.FromSeconds(90));
        client.Timeout.Should().Be(
            HttpResilienceDefaults.TotalRequestTimeout,
            "the transport timeout and the resilience budget must move together");
    }

    [Fact]
    public void ApiClient_KeepsItsConfiguredBaseAddressAndJsonAccept()
    {
        using var provider = BuildProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("APIClient");

        client.BaseAddress.Should().Be(new Uri("https://localhost:6001", UriKind.Absolute));
        client.DefaultRequestHeaders.Accept.Should().ContainSingle()
            .Which.MediaType.Should().Be("application/json");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingApiEndpoint_FailsTheOptionsValidation(string? endpoint)
    {
        // The Required annotation plus ValidateDataAnnotations and ValidateOnStart form the one guard,
        // so the client factory no longer repeats it with a hand-written throw.
        using var provider = BuildProvider(endpoint);

        var act = () => provider.GetRequiredService<IOptions<ApiSettings>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void MissingApiEndpoint_StillFailsWhenTheApiClientIsCreated()
    {
        using var provider = BuildProvider(string.Empty);

        var act = () =>
        {
            using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("APIClient");
        };

        act.Should().Throw<OptionsValidationException>();
    }

    // Item 12: audit-history client
    [Fact]
    public void AddAuditHistoryUI_RegistersScopedService_Idempotent()
    {
        var services = new ServiceCollection();

        services.AddAuditHistoryUI();
        services.AddAuditHistoryUI();

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(IAuditHistoryUIService)).Subject;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<AuditHistoryService>();

        // TryAddScoped: an app's own registration made first is kept.
        var custom = new ServiceCollection();
        custom.AddScoped<IAuditHistoryUIService, AppAuditHistoryService>();
        custom.AddAuditHistoryUI();
        custom.Should().ContainSingle(d => d.ServiceType == typeof(IAuditHistoryUIService))
            .Which.ImplementationType.Should().Be<AppAuditHistoryService>();
    }

    // Item 38: offline write queue
    [Fact]
    public void AddOfflineWriteQueue_RegistersScopedQueue_AppliesConfigure()
    {
        var services = new ServiceCollection();

        services.AddOfflineWriteQueue(options =>
        {
            options.StorageKey = "app.offline";
            options.MaxPendingWrites = 5;
        });
        services.AddOfflineWriteQueue();

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(IOfflineWriteQueue)).Subject;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped, "the browser connectivity service and cache store it depends on are scoped");
        descriptor.ImplementationType.Should().Be<OfflineWriteQueue>();
        services.Should().Contain(d => d.ServiceType == typeof(TimeProvider));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OfflineWriteQueueOptions>>().Value;
        options.StorageKey.Should().Be("app.offline");
        options.MaxPendingWrites.Should().Be(5);
        options.MaxAge.Should().Be(TimeSpan.FromHours(24), "an unconfigured option keeps its default");
    }

    [Fact]
    public void AddOfflineWriteHandler_AddsEachHandlerOnce()
    {
        var services = new ServiceCollection();

        services.AddOfflineWriteHandler<CreateWidgetReplayHandler>();
        services.AddOfflineWriteHandler<CreateWidgetReplayHandler>();
        services.AddOfflineWriteHandler<DeleteWidgetReplayHandler>();

        var handlers = services.Where(d => d.ServiceType == typeof(IOfflineWriteHandler)).ToList();
        Type[] expected = [typeof(CreateWidgetReplayHandler), typeof(DeleteWidgetReplayHandler)];
        handlers.Select(d => d.ImplementationType).Should().BeEquivalentTo(expected);
        handlers.Should().AllSatisfy(d => d.Lifetime.Should().Be(ServiceLifetime.Scoped));
    }

    private sealed class AppAuditHistoryService : IAuditHistoryUIService
    {
        public Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>> GetPageAsync(
            string routePrefix,
            string entityAlias,
            string entityKey,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([]));
    }

    private sealed class CreateWidgetReplayHandler : IOfflineWriteHandler
    {
        public string Kind => "Tests.Widget.Create.v1";

        public Task<Result> ReplayAsync(PendingWrite write, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class DeleteWidgetReplayHandler : IOfflineWriteHandler
    {
        public string Kind => "Tests.Widget.Delete.v1";

        public Task<Result> ReplayAsync(PendingWrite write, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
