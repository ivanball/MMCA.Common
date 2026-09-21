using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Providers;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// The configuration overload selects the provider by name from whatever
/// <see cref="IAiProviderFactory"/> instances the host registered. These tests drive that path with
/// fake factories, so they prove the selection, the case rule and the two failure messages without a
/// vendor package in the loop; the adapters themselves are covered in <c>Providers/</c>.
/// </summary>
public sealed class AiProviderSelectionTests
{
    private static IConfiguration EnabledConfiguration(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Ai:Enabled"] = "true",
                ["Ai:Provider"] = provider,
                ["Ai:Model"] = "any-model",
                ["Ai:ApiKey"] = "test-key",
            })
            .Build();

    private static ServiceProvider Build(IConfiguration configuration, params IAiProviderFactory[] factories)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var factory in factories)
        {
            // Plain AddSingleton: the fakes share one implementation type, and TryAddEnumerable
            // would keep only the first of them.
            services.AddSingleton<IAiProviderFactory>(factory);
        }

        services.AddMmcaChatClient(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfiguredProvider_SelectsTheFactoryWithThatName()
    {
        using var inner = new StubChatClient();
        var chosen = new FakeProviderFactory("Fake", inner);
        var other = new FakeProviderFactory("Other", new StubChatClient());
        using var provider = Build(EnabledConfiguration("Fake"), other, chosen);

        var client = provider.GetRequiredService<IChatClient>();

        client.Should().BeOfType<BoundedChatClient>();
        client.GetService(typeof(StubChatClient)).Should().BeSameAs(inner);
        chosen.Created.Should().Be(1);
        other.Created.Should().Be(0);
    }

    [Fact]
    public void ConfiguredProvider_MatchesIgnoringCase()
    {
        using var inner = new StubChatClient();
        using var provider = Build(EnabledConfiguration("fAkE"), new FakeProviderFactory("Fake", inner));

        provider.GetRequiredService<IChatClient>().GetService(typeof(StubChatClient)).Should().BeSameAs(inner);
    }

    [Fact]
    public void ConfiguredProvider_ReceivesTheValidatedSettings()
    {
        using var inner = new StubChatClient();
        var factory = new FakeProviderFactory("Fake", inner);
        using var provider = Build(EnabledConfiguration("Fake"), factory);

        provider.GetRequiredService<IChatClient>();

        factory.LastSettings.Should().NotBeNull();
        factory.LastSettings!.Model.Should().Be("any-model");
        factory.LastSettings.ApiKey.Should().Be("test-key");
    }

    [Fact]
    public void UnknownProvider_FailsOptionsValidationNamingTheRegisteredOnes()
    {
        using var provider = Build(
            EnabledConfiguration("Nowhere"),
            new FakeProviderFactory("Fake", new StubChatClient()),
            new FakeProviderFactory("Other", new StubChatClient()));

        var act = () => provider.GetRequiredService<IOptions<AiSettings>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*'Nowhere'*")
            .WithMessage("*Fake, Other*", "the fix is in the message: the names a host may pick from");
    }

    [Fact]
    public void NoRegisteredProvider_FailsOptionsValidationNamingTheAdapterPackages()
    {
        using var provider = Build(EnabledConfiguration("Anthropic"));

        var act = () => provider.GetRequiredService<IOptions<AiSettings>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*MMCA.Common.AI.Anthropic*")
            .WithMessage("*MMCA.Common.AI.OpenAI*")
            .WithMessage("*Func<IServiceProvider, IChatClient>*");
    }

    [Fact]
    public void Disabled_NeverConsultsTheFactories()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Ai:Enabled"] = "false" })
            .Build();
        using var provider = Build(configuration);

        provider.GetRequiredService<IOptions<AiSettings>>().Value.Enabled.Should().BeFalse();
        provider.GetService<IChatClient>().Should().BeNull();
    }

    [Fact]
    public void FactoryOverload_DoesNotMatchTheNameAgainstAnything()
    {
        // The name is only the metrics fallback on this path: nothing is registered under it and
        // the pipeline still builds.
        var services = new ServiceCollection();
        services.AddLogging();
        using var inner = new StubChatClient();
        services.AddMmcaChatClient(EnabledConfiguration("Nowhere"), _ => inner);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>().GetService(typeof(StubChatClient)).Should().BeSameAs(inner);
    }

    [Fact]
    public void Pipeline_CarriesThePromptTaggingLayerInsideTelemetry()
    {
        using var inner = new StubChatClient();
        using var provider = Build(EnabledConfiguration("Fake"), new FakeProviderFactory("Fake", inner));

        var client = provider.GetRequiredService<IChatClient>();

        client.GetService(typeof(PromptTaggingChatClient)).Should().NotBeNull();
        client.GetService(typeof(OpenTelemetryChatClient)).Should().NotBeNull();
    }

    private sealed class FakeProviderFactory(string name, IChatClient client) : IAiProviderFactory
    {
        public string Name => name;

        public int Created { get; private set; }

        public AiSettings? LastSettings { get; private set; }

        public IChatClient Create(AiSettings settings, IServiceProvider serviceProvider)
        {
            Created++;
            LastSettings = settings;
            return client;
        }
    }
}
