using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.AI.Anthropic;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.OpenAI;
using MMCA.Common.AI.Providers;

namespace MMCA.Common.AI.Tests.Providers;

/// <summary>
/// Each adapter package is one factory and one registration call. These tests construct the real
/// SDK clients with a fake key and assert their shape and the provider name they report, never a
/// request: constructing a client opens no connection, so nothing here reaches a network.
/// </summary>
public sealed class AdapterFactoryTests
{
    private static readonly ServiceProvider EmptyServices = new ServiceCollection().BuildServiceProvider();

    private static AiSettings Settings(string provider, string model, Uri? endpoint = null) => new()
    {
        Enabled = true,
        Provider = provider,
        Model = model,
        ApiKey = "test-key",
        Endpoint = endpoint,
        Timeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public void Anthropic_Create_ReturnsAClientReportingItsProviderAndModel()
    {
        using var client = new AnthropicAiProviderFactory().Create(Settings("Anthropic", "claude-haiku-4-5"), EmptyServices);

        var metadata = client.GetService<ChatClientMetadata>();

        metadata.Should().NotBeNull();
        metadata!.ProviderName.Should().Be("anthropic");
        metadata.DefaultModelId.Should().Be("claude-haiku-4-5");
        UsageRecordingChatClient.ResolveProviderName(client, "Anthropic").Should().Be("anthropic",
            because: "the metrics tag comes from the client, not the configuration");
    }

    [Fact]
    public void OpenAi_Create_ReturnsAClientReportingItsProviderAndModel()
    {
        using var client = new OpenAiProviderFactory().Create(Settings("OpenAI", "gpt-5"), EmptyServices);

        var metadata = client.GetService<ChatClientMetadata>();

        metadata.Should().NotBeNull();
        metadata!.ProviderName.Should().Be("openai");
        metadata.DefaultModelId.Should().Be("gpt-5");
        UsageRecordingChatClient.ResolveProviderName(client, "OpenAI").Should().Be("openai");
    }

    [Fact]
    public void OpenAi_Create_HonorsAConfiguredEndpoint()
    {
        var endpoint = new Uri("https://gateway.example.com/openai/v1/");
        using var client = new OpenAiProviderFactory().Create(Settings("OpenAI", "gpt-5", endpoint), EmptyServices);

        client.GetService<ChatClientMetadata>()!.ProviderUri.Should().Be(endpoint);
    }

    [Fact]
    public void Anthropic_Create_HonorsAConfiguredEndpoint()
    {
        var endpoint = new Uri("https://gateway.example.com/anthropic/");
        using var client = new AnthropicAiProviderFactory().Create(Settings("Anthropic", "claude-haiku-4-5", endpoint), EmptyServices);

        client.GetService<ChatClientMetadata>()!.ProviderUri.Should().Be(endpoint);
    }

    [Fact]
    public void Names_AreTheConfigurationValuesTheAdaptersDocument()
    {
        new AnthropicAiProviderFactory().Name.Should().Be("Anthropic");
        new OpenAiProviderFactory().Name.Should().Be("OpenAI");
    }

    [Theory]
    [InlineData("Anthropic", typeof(AnthropicAiProviderFactory))]
    [InlineData("openai", typeof(OpenAiProviderFactory))]
    public void Registration_MakesTheProviderSelectableByName(string configured, Type expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Ai:Enabled"] = "true",
                ["Ai:Provider"] = configured,
                ["Ai:Model"] = "any-model",
                ["Ai:ApiKey"] = "test-key",

                // Adapter selection, not guardrail policy: opted out here so the default stays
                // pinned in one dedicated place (Guardrails/GuardrailRegistrationTests).
                ["Ai:RequireGuardrail"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnthropicAiProvider().AddOpenAiProvider();
        services.AddMmcaChatClient(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IAiProviderFactory>().Select(factory => factory.GetType())
            .Should().BeEquivalentTo([typeof(AnthropicAiProviderFactory), typeof(OpenAiProviderFactory)]);

        var client = provider.GetRequiredService<IChatClient>();

        client.Should().BeOfType<BoundedChatClient>();
        client.GetService<ChatClientMetadata>()!.ProviderName.Should().Be(
            expected == typeof(AnthropicAiProviderFactory) ? "anthropic" : "openai");
    }

    [Fact]
    public void Registration_IsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddAnthropicAiProvider().AddAnthropicAiProvider();

        services.Count(descriptor => descriptor.ServiceType == typeof(IAiProviderFactory)).Should().Be(1);
    }
}
