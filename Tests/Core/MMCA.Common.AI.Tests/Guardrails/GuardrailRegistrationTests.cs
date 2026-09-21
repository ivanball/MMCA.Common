using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Guardrails;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// The guardrail requirement is a DEFAULT, not a suggestion: an enabled host that registered nothing
/// to inspect its model traffic fails at registration, and the failure names the one-line fix. These
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
            .WithMessage("*IChatGuardrail*", "the contract to implement is named")
            .WithMessage("*AddPiiRedactionGuardrail()*", "the one-line fix is in the message");
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
    public void Enabled_WithTheShippedRedactionGuardrail_RegistersTheClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPiiRedactionGuardrail();
        using var inner = new StubChatClient();

        services.AddMmcaChatClient(Configuration(), _ => inner);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>()
            .GetService(typeof(GuardrailChatClient)).Should().NotBeNull();
    }

    [Fact]
    public void Enabled_WithOnlyARedactor_SatisfiesTheRequirementAndComposesTheLayer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatRequestRedactor>(new PiiRedactionGuardrail());
        using var inner = new StubChatClient();

        services.AddMmcaChatClient(Configuration(), _ => inner);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>()
            .GetService(typeof(GuardrailChatClient)).Should().NotBeNull(
                because: "a redactor rewrites every request, which is inspection with teeth");
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
}
