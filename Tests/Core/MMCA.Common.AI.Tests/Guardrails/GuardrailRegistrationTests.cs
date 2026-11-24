using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Guardrails;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// The guardrail requirement is a DEFAULT, not a suggestion: an enabled host that registered no
/// prompt-injection defense fails at registration, and the failure names the one-line fix. These
/// are the facts the rest of the suite opts out of with <c>Ai:RequireGuardrail=false</c>, so they are
/// pinned here once.
/// </summary>
public sealed class GuardrailRegistrationTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] extra) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new[]
                {
                    ("Ai:Enabled", "true"),
                    ("Ai:Provider", "Stub"),
                    ("Ai:Model", "claude-haiku-4-5"),
                    ("Ai:ApiKey", "test-key"),
                }
                .Concat(extra)
                .ToDictionary(entry => entry.Item1, entry => (string?)entry.Item2, StringComparer.Ordinal))
            .Build();

    [Fact]
    // The whole point of the default: shipping a model call with nothing inspecting it is a
    // deployment failure, not a finding somebody writes up later.
    public void Enabled_WithNothingInspectingTheTraffic_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(Configuration(), _ => inner);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Ai:RequireGuardrail*", "the setting that relaxes it is named")
            .WithMessage("*IPromptInjectionGuardrail*", "the marker a custom defense carries is named")
            .WithMessage("*AddContentPolicyGuardrail(configuration)*", "the one-line fix is in the message");
    }

    [Fact]
    public void Enabled_WithTheRequirementTurnedOff_RegistersTheClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var inner = new StubChatClient();

        services.AddMmcaChatClient(Configuration(("Ai:RequireGuardrail", "false")), _ => inner);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>().Should().BeOfType<BoundedChatClient>();
        provider.GetRequiredService<IChatClient>().GetService(typeof(GuardrailChatClient)).Should().BeNull(
            because: "a host that opted out gets no layer, down to the type the container hands back");
    }

    [Fact]
    // A PII redactor strips contact details but lets "ignore previous instructions" straight
    // through, so on its own it is not the defense the requirement asks for.
    public void Enabled_WithOnlyThePiiRedactionGuardrail_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPiiRedactionGuardrail();
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(Configuration(), _ => inner);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddPiiRedactionGuardrail() alone does not satisfy*", "the message says why the PII redactor is not enough");
    }

    [Fact]
    public void Enabled_WithAnUnmarkedGuardrailAndRedactor_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatRequestRedactor>(new PiiRedactionGuardrail());
        services.AddSingleton<IChatGuardrail>(new PiiRedactionGuardrail());
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(Configuration(), _ => inner);

        act.Should().Throw<InvalidOperationException>(
            "having something in the guardrail layer is not the same as defending against prompt injection");
    }

    [Fact]
    public void Enabled_WithTheShippedContentPolicyGuardrail_RegistersTheClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentPolicyGuardrail(Configuration());
        using var inner = new StubChatClient();

        services.AddMmcaChatClient(Configuration(), _ => inner);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>()
            .GetService(typeof(GuardrailChatClient)).Should().NotBeNull();
    }

    [Fact]
    public void Enabled_WithTheContentPolicyAndThePiiRedactor_RegistersTheClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPiiRedactionGuardrail();
        services.AddContentPolicyGuardrail(Configuration());
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(Configuration(), _ => inner);

        act.Should().NotThrow("the content policy is the injection defense; the PII redactor rides alongside it");
    }

    [Theory]
    [InlineData("type")]
    [InlineData("instance")]
    [InlineData("typed-factory")]
    // A host's own injection defense counts as long as the registration names its type: by
    // implementation type, by instance, or by a factory typed as the implementation.
    public void Enabled_WithACustomMarkedGuardrail_RegistersTheClient(string registration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _ = registration switch
        {
            "type" => services.AddSingleton<IChatGuardrail, MarkedInjectionGuardrail>(),
            "instance" => services.AddSingleton<IChatGuardrail>(new MarkedInjectionGuardrail()),
            _ => services.AddSingleton<IChatGuardrail, MarkedInjectionGuardrail>(_ => new MarkedInjectionGuardrail()),
        };
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(Configuration(), _ => inner);

        act.Should().NotThrow("a guardrail marked IPromptInjectionGuardrail is the defense the requirement asks for");
    }

    [Fact]
    public void Enabled_WithAKeyedMarkedGuardrail_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<IChatGuardrail, MarkedInjectionGuardrail>("elsewhere");
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(Configuration(), _ => inner);

        act.Should().Throw<InvalidOperationException>(
            "the guardrail layer resolves unkeyed guardrails only, so a keyed one never runs");
    }

    [Fact]
    public void Disabled_NeverRequiresAGuardrail()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Ai:Enabled"] = "false" })
                .Build(),
            _ => inner);

        act.Should().NotThrow("the section ships in every appsettings file, switched off");
    }

    [Fact]
    // One instance under both contracts: the redactor and the guardrail are two faces of one
    // decision, and two copies would diverge the moment the type carried state.
    public void AddPiiRedactionGuardrail_ResolvesOneInstanceUnderBothContracts()
    {
        var services = new ServiceCollection();

        services.AddPiiRedactionGuardrail().AddPiiRedactionGuardrail();
        using var provider = services.BuildServiceProvider();

        var guardrails = provider.GetServices<IChatGuardrail>().ToArray();
        var redactors = provider.GetServices<IChatRequestRedactor>().ToArray();

        guardrails.Should().ContainSingle();
        redactors.Should().ContainSingle();
        redactors[0].Should().BeSameAs(guardrails[0]);
    }

    [Fact]
    public void TheDefault_IsToRequireAGuardrail() =>
        new AiSettings().RequireGuardrail.Should().BeTrue(
            "an absence nobody configured must read as the safe answer");

    [Fact]
    public void OnlyTheContentPolicy_OfTheShippedGuardrails_IsAPromptInjectionDefense()
    {
        typeof(IPromptInjectionGuardrail).IsAssignableFrom(typeof(ContentPolicyGuardrail)).Should().BeTrue();
        typeof(IPromptInjectionGuardrail).IsAssignableFrom(typeof(PiiRedactionGuardrail)).Should().BeFalse();
    }

    /// <summary>A host-written injection defense: allows everything, but carries the marker.</summary>
    private sealed class MarkedInjectionGuardrail : IChatGuardrail, IPromptInjectionGuardrail
    {
        public ValueTask<GuardrailVerdict> InspectRequestAsync(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);

        public ValueTask<GuardrailVerdict> InspectResponseAsync(
            ChatResponse response,
            ChatOptions? options,
            CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);
    }
}
