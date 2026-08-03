using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Resilience;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the resilience-relevant configuration of the named <c>"APIClient"</c> registered by
/// <c>AddUIShared</c>: its total timeout must come from the shared budget rather than
/// <see cref="HttpClient"/>'s uncoordinated 100s default, which would otherwise cut a call off at
/// an arbitrary point inside the retry pipeline.
/// </summary>
public sealed class ApiClientRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:ApiEndpoint"] = "https://localhost:6001" })
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
}
