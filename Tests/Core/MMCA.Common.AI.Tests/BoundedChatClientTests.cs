using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// Each bound is asserted from the innermost client's point of view: what the provider was actually
/// asked to do, not what the caller said. A bound that only changes the caller's copy of the options
/// is not a bound.
/// </summary>
public sealed class BoundedChatClientTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new(ChatRole.User, "score this")];

    private static AiSettings Settings(
        int maxOutputTokens = 256,
        bool allowTools = false,
        int? budget = null,
        TimeSpan? timeout = null) =>
        new()
        {
            Enabled = true,
            Model = "claude-haiku-4-5",
            ApiKey = "key",
            MaxOutputTokens = maxOutputTokens,
            AllowTools = allowTools,
            PerCallInputTokenBudget = budget,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

    [Fact]
    public async Task GetResponseAsync_ClampsAnOverAmbitiousOutputCeiling()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(maxOutputTokens: 256));

        await client.GetResponseAsync(Prompt, new ChatOptions { MaxOutputTokens = 100_000 }, TestContext.Current.CancellationToken);

        inner.LastOptions!.MaxOutputTokens.Should().Be(256);
    }

    [Fact]
    public async Task GetResponseAsync_KeepsACallersSmallerCeiling()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(maxOutputTokens: 256));

        await client.GetResponseAsync(Prompt, new ChatOptions { MaxOutputTokens = 32 }, TestContext.Current.CancellationToken);

        inner.LastOptions!.MaxOutputTokens.Should().Be(32);
    }

    [Fact]
    public async Task GetResponseAsync_AppliesTheCeilingWhenTheCallerAsksForNothing()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(maxOutputTokens: 256));

        await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        inner.LastOptions!.MaxOutputTokens.Should().Be(256);
    }

    [Fact]
    public async Task GetResponseAsync_DoesNotMutateTheCallersOptions()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(maxOutputTokens: 256));
        var callerOptions = new ChatOptions { MaxOutputTokens = 100_000 };

        await client.GetResponseAsync(Prompt, callerOptions, TestContext.Current.CancellationToken);

        callerOptions.MaxOutputTokens.Should().Be(100_000);
        inner.LastOptions.Should().NotBeSameAs(callerOptions);
    }

    [Fact]
    public async Task GetResponseAsync_StripsToolsWhenToolUseIsNotAllowed()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: false));
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => 42, "answer")],
            ToolMode = ChatToolMode.RequireAny,
        };

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().BeNull();
        inner.LastOptions.ToolMode.Should().BeNull();
        options.Tools.Should().HaveCount(1, because: "the caller's own options are never mutated");
    }

    [Fact]
    public async Task GetResponseAsync_KeepsToolsWhenToolUseIsAllowed()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings(allowTools: true));
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => 42, "answer")] };

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.LastOptions!.Tools.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetResponseAsync_TimesOutOnItsOwnBudget()
    {
        using var inner = new StubChatClient(respond: async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            return new ChatResponse();
        });
        using var client = new BoundedChatClient(inner, Settings(timeout: TimeSpan.FromMilliseconds(50)));

        var act = async () => await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetResponseAsync_RefusesARequestOverTheInputBudget()
    {
        using var inner = new StubChatClient(service: new FixedTokenEstimator(5_000));
        using var client = new BoundedChatClient(inner, Settings(budget: 1_000));

        var act = async () => await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*PerCallInputTokenBudget*");
        inner.CallCount.Should().Be(0, because: "a refused request must never reach the provider");
    }

    [Fact]
    public async Task GetResponseAsync_AllowsARequestInsideTheInputBudget()
    {
        using var inner = new StubChatClient(service: new FixedTokenEstimator(10));
        using var client = new BoundedChatClient(inner, Settings(budget: 1_000));

        await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetResponseAsync_SkipsTheBudgetCheckWhenNoBudgetIsConfigured()
    {
        using var inner = new StubChatClient(service: new FixedTokenEstimator(int.MaxValue));
        using var client = new BoundedChatClient(inner, Settings(budget: null));

        await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ClampsAndStripsTheSameWay()
    {
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        using var client = new BoundedChatClient(inner, Settings(maxOutputTokens: 256));
        var options = new ChatOptions
        {
            MaxOutputTokens = 100_000,
            Tools = [AIFunctionFactory.Create(() => 42, "answer")],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(Prompt, options, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        updates.Should().ContainSingle();
        inner.LastOptions!.MaxOutputTokens.Should().Be(256);
        inner.LastOptions.Tools.Should().BeNull();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RefusesOverBudgetBeforeEnumerationStarts()
    {
        using var inner = new StubChatClient(service: new FixedTokenEstimator(5_000));
        using var client = new BoundedChatClient(inner, Settings(budget: 1_000));

        var act = () => client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        act.Should().Throw<InvalidOperationException>();
        await Task.CompletedTask;
    }

    [Fact]
    public void EstimateInputTokens_FallsBackToFourCharactersPerToken()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, new string('x', 40)) };

        BoundedChatClient.EstimateInputTokens(messages, options: null, estimator: null).Should().Be(10);
    }

    [Fact]
    public void EstimateInputTokens_CountsTheInstructionsToo()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, new string('x', 40)) };
        var options = new ChatOptions { Instructions = new string('y', 40) };

        BoundedChatClient.EstimateInputTokens(messages, options, estimator: null).Should().Be(20);
    }

    [Fact]
    public void EstimateInputTokens_PrefersASuppliedEstimator()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "anything") };

        BoundedChatClient.EstimateInputTokens(messages, options: null, new FixedTokenEstimator(7)).Should().Be(7);
    }

    [Fact]
    public async Task GetResponseAsync_SendsThePinnedModelByName()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings());

        await client.GetResponseAsync(Prompt, new ChatOptions(), TestContext.Current.CancellationToken);

        inner.LastOptions!.ModelId.Should().Be("claude-haiku-4-5",
            because: "every adapter is asked for the pinned model explicitly, whether or not it honors a per-request override");
    }

    [Fact]
    public async Task GetResponseAsync_AcceptsARequestNamingThePinnedModel()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings());
        var options = new PromptContract("session-scoring", "3", "claude-haiku-4-5", "be terse").ToChatOptions();

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetResponseAsync_RefusesARequestNamingAnotherModel()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings());
        var options = new PromptContract("session-scoring", "3", "gpt-5", "be terse").ToChatOptions();

        var act = () => client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'gpt-5'*")
            .WithMessage("*Ai:Model*")
            .WithMessage("*'claude-haiku-4-5'*");
        inner.CallCount.Should().Be(0, "a model mismatch is refused before the provider is called");
    }

    [Fact]
    public void GetStreamingResponseAsync_RefusesARequestNamingAnotherModelEagerly()
    {
        using var inner = new StubChatClient();
        using var client = new BoundedChatClient(inner, Settings());
        var options = new PromptContract("session-scoring", "3", "gpt-5", "be terse").ToChatOptions();

        var act = () => client.GetStreamingResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        act.Should().Throw<InvalidOperationException>("the refusal happens when the request is made, not when the stream is read");
    }

    [Fact]
    public async Task GetResponseAsync_WithNoPinnedModel_LeavesTheRequestsModelAlone()
    {
        using var inner = new StubChatClient();
        var settings = new AiSettings { Enabled = true, Provider = "Stub", ApiKey = "key", MaxOutputTokens = 256 };
        using var client = new BoundedChatClient(inner, settings);

        await client.GetResponseAsync(Prompt, new ChatOptions { ModelId = "anything" }, TestContext.Current.CancellationToken);

        inner.LastOptions!.ModelId.Should().Be("anything");
    }
}
