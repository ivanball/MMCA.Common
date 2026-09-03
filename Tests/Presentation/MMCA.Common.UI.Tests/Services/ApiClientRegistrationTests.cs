using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Resilience;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth.Tokens;

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
}
