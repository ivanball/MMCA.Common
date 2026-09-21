using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Guardrails;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// Which tools the model was actually offered is asserted from the innermost client's point of view.
/// A tool the caller attached and the policy layer removed must be absent from
/// <c>LastOptions.Tools</c>: a gate that only changes the caller's copy is not a gate.
/// </summary>
public sealed class ToolPolicyTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new(ChatRole.User, "do the thing")];

    private static AiSettings Settings(bool allowTools) => new()
    {
        Enabled = true,
        Model = "claude-haiku-4-5",
        ApiKey = "key",
        MaxOutputTokens = 256,
        AllowTools = allowTools,
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static ChatOptions WithTools(params AITool[] tools) => new()
    {
        Tools = [.. tools],
        ToolMode = ChatToolMode.Auto,
    };

    [Fact]
    public async Task AllowToolsFalse_StripsEveryToolWhateverThePoliciesSay()
    {
        var policy = new StubToolPolicy();
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: false), [policy]);

        await client.GetResponseAsync(Prompt, WithTools(new StubTool("lookup")), TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().BeNull();
        inner.LastOptions.ToolMode.Should().BeNull();
        policy.Authorizations.Should().Be(0, "the switch is outside the policy, not behind it");
    }

    [Fact]
    // Fail closed: an unanswered question about a capability is answered by withholding it.
    public async Task AllowToolsTrue_WithNoPolicyRegistered_StripsEveryTool()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: true));

        await client.GetResponseAsync(Prompt, WithTools(new StubTool("lookup")), TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().BeNull();
        inner.LastOptions.ToolMode.Should().BeNull("a tool mode with no tools is a request no provider can honor");
    }

    [Fact]
    public async Task AnAllowingPolicy_PassesANonConsequentialTool()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: true), [new StubToolPolicy()]);
        var options = WithTools(new StubTool("lookup"));

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().ContainSingle().Which.Name.Should().Be("lookup");
        inner.LastOptions.ToolMode.Should().Be(ChatToolMode.Auto);
        options.Tools.Should().HaveCount(1, "the caller's own options are never mutated");
        inner.LastOptions.Should().NotBeSameAs(options);
    }

    [Fact]
    public async Task ADenyingPolicy_StripsTheTool()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(
            inner,
            Settings(allowTools: true),
            [new StubToolPolicy(), new StubToolPolicy(ToolAuthorization.Denied)]);

        await client.GetResponseAsync(Prompt, WithTools(new StubTool("lookup")), TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().BeNull("policies compose by intersection: every one has to allow");
    }

    [Fact]
    // Reading is recoverable and writing is not, so a consequential tool clears one more bar: the
    // caller confirmed it for THIS request.
    public async Task AConsequentialTool_IsStrippedWithoutConfirmation()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: true), [new StubToolPolicy()]);

        await client.GetResponseAsync(
            Prompt,
            WithTools(new StubTool("lookup"), new StubTool("send_email", consequential: true)),
            TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().ContainSingle().Which.Name.Should().Be("lookup");
    }

    [Fact]
    public async Task AConsequentialTool_IsOfferedOnceTheRequestConfirmsIt()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: true), [new StubToolPolicy()]);
        var options = WithTools(new StubTool("send_email", consequential: true));
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatToolPolicy.ConfirmedToolsPropertyKey] = new[] { "send_email" },
        };

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().ContainSingle().Which.Name.Should().Be("send_email");
    }

    [Fact]
    public async Task AConfirmedToolAPolicyDenies_StaysDenied()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(
            inner,
            Settings(allowTools: true),
            [new StubToolPolicy(ToolAuthorization.Denied)]);
        var options = WithTools(new StubTool("send_email", consequential: true));
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatToolPolicy.ConfirmedToolsPropertyKey] = "send_email",
        };

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().BeNull(
            "a human saying yes is not a grant of an authority the application never had");
    }

    [Fact]
    public async Task AConfirmationNamingAnotherTool_DoesNotConfirmThisOne()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: true), [new StubToolPolicy()]);
        var options = WithTools(new StubTool("send_email", consequential: true));
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatToolPolicy.ConfirmedToolsPropertyKey] = new[] { "delete_order" },
        };

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().BeNull();
    }

    [Fact]
    public void Registration_RefusesAllowToolsWithNoPolicy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPiiRedactionGuardrail();
        using var inner = new StubChatClient();

        var act = () => services.AddMmcaChatClient(EnabledConfiguration(("Ai:AllowTools", "true")), _ => inner);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Ai:AllowTools*")
            .WithMessage("*IChatToolPolicy*");
    }

    [Fact]
    public void Registration_AcceptsAllowToolsWithAPolicy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPiiRedactionGuardrail();
        services.AddSingleton<IChatToolPolicy>(new StubToolPolicy());
        using var inner = new StubChatClient();

        services.AddMmcaChatClient(EnabledConfiguration(("Ai:AllowTools", "true")), _ => inner);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>().Should().BeOfType<BoundedChatClient>();
    }

    [Fact]
    public async Task Registration_WiresTheRegisteredPoliciesIntoTheBuiltClient()
    {
        var policy = new StubToolPolicy(ToolAuthorization.Denied);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPiiRedactionGuardrail();
        services.AddSingleton<IChatToolPolicy>(policy);
        using var inner = new StubChatClient();
        services.AddMmcaChatClient(EnabledConfiguration(("Ai:AllowTools", "true")), _ => inner);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IChatClient>().GetResponseAsync(
            Prompt,
            WithTools(new StubTool("lookup")),
            TestContext.Current.CancellationToken);

        policy.Authorizations.Should().Be(1, "the registered instance is the one the layer consults");
        inner.LastOptions!.Tools.Should().BeNull();
    }

    private static IConfiguration EnabledConfiguration(params (string Key, string Value)[] extra) =>
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
}
