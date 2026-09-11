using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// Registration is asserted through the factory overload, so the pipeline is exercised end to end
/// with no credential and no network. The shape assertions walk the built client with
/// <c>GetService</c>, which is how a delegating pipeline reports what is in it.
/// </summary>
public sealed class AiServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.Ordinal))
            .Build();

    private static IConfiguration EnabledConfiguration(params (string Key, string Value)[] extra) =>
        Configuration([
            ("Ai:Enabled", "true"),
            ("Ai:Model", "claude-haiku-4-5"),
            ("Ai:ApiKey", "test-key"),
            ("Ai:MaxOutputTokens", "256"),
            .. extra,
        ]);

    private static ServiceProvider Build(IConfiguration configuration, StubChatClient inner, bool withDistributedCache = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (withDistributedCache)
        {
            services.AddDistributedMemoryCache();
        }

        services.AddMmcaChatClient(configuration, _ => inner);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Disabled_RegistersNoChatClient()
    {
        using var inner = new StubChatClient();
        using var provider = Build(Configuration(("Ai:Enabled", "false")), inner);

        provider.GetService<IChatClient>().Should().BeNull(
            because: "a consumer gates on the client being resolvable, not on reading a flag");
    }

    [Fact]
    public void Absent_SectionRegistersNoChatClient()
    {
        using var inner = new StubChatClient();
        using var provider = Build(Configuration(), inner);

        provider.GetService<IChatClient>().Should().BeNull();
    }

    [Fact]
    public void Disabled_StillBindsAndExposesTheSettings()
    {
        using var inner = new StubChatClient();
        using var provider = Build(Configuration(("Ai:Enabled", "false")), inner);

        provider.GetRequiredService<IOptions<AiSettings>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Enabled_BuildsTheGovernancePipelineWithBoundsOutermost()
    {
        using var inner = new StubChatClient();
        using var provider = Build(EnabledConfiguration(), inner);

        var client = provider.GetRequiredService<IChatClient>();

        client.Should().BeOfType<BoundedChatClient>(
            because: "nothing downstream may be asked to do what the configuration forbids");
        client.GetService(typeof(UsageRecordingChatClient)).Should().NotBeNull();
        client.GetService(typeof(StubChatClient)).Should().BeSameAs(inner);
    }

    [Fact]
    public void Enabled_WithoutCacheSettings_AddsNoCachingLayer()
    {
        using var inner = new StubChatClient();
        using var provider = Build(EnabledConfiguration(), inner, withDistributedCache: true);

        provider.GetRequiredService<IChatClient>()
            .GetService(typeof(DistributedCachingChatClient)).Should().BeNull();
    }

    [Fact]
    public void EnableCache_WithoutADistributedCache_AddsNoCachingLayer()
    {
        using var inner = new StubChatClient();
        using var provider = Build(EnabledConfiguration(("Ai:EnableCache", "true")), inner);

        provider.GetRequiredService<IChatClient>()
            .GetService(typeof(DistributedCachingChatClient)).Should().BeNull(
                because: "a cache layer over a store nobody registered would throw on the first call");
    }

    [Fact]
    public void EnableCache_WithADistributedCache_AddsTheCachingLayer()
    {
        using var inner = new StubChatClient();
        using var provider = Build(EnabledConfiguration(("Ai:EnableCache", "true")), inner, withDistributedCache: true);

        provider.GetRequiredService<IChatClient>()
            .GetService(typeof(DistributedCachingChatClient)).Should().NotBeNull();
    }

    [Fact]
    public async Task Enabled_AppliesTheConfiguredBoundsToARealCall()
    {
        using var inner = new StubChatClient();
        await using var provider = Build(EnabledConfiguration(), inner);
        var client = provider.GetRequiredService<IChatClient>();

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "score this")],
            new ChatOptions { MaxOutputTokens = 100_000 },
            TestContext.Current.CancellationToken);

        inner.LastOptions!.MaxOutputTokens.Should().Be(256);
    }

    [Fact]
    public void Enabled_WithoutAModel_FailsValidation()
    {
        using var inner = new StubChatClient();
        using var provider = Build(Configuration(("Ai:Enabled", "true"), ("Ai:ApiKey", "test-key")), inner);

        var act = () => provider.GetRequiredService<IOptions<AiSettings>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*Model*");
    }

    [Fact]
    public void Enabled_WithoutAnApiKey_FailsValidation()
    {
        using var inner = new StubChatClient();
        using var provider = Build(Configuration(("Ai:Enabled", "true"), ("Ai:Model", "claude-haiku-4-5")), inner);

        var act = () => provider.GetRequiredService<IOptions<AiSettings>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*ApiKey*");
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        var services = new ServiceCollection();
        using var inner = new StubChatClient();

        var nullConfiguration = () => services.AddMmcaChatClient(null!, _ => inner);
        var nullFactory = () => services.AddMmcaChatClient(Configuration(), null!);

        nullConfiguration.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();
    }
}
