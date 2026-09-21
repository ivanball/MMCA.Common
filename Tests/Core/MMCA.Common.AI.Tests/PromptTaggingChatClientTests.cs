using System.Diagnostics;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Observability;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// The tagging layer writes the prompt identity onto the activity the pipeline's own telemetry
/// source started, and onto nothing else: an activity from any other source is left untouched, so a
/// host that never subscribed the AI source gets no stray attributes on its request span.
/// </summary>
public sealed class PromptTaggingChatClientTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new(ChatRole.User, "score this")];

    [Fact]
    public async Task GetResponseAsync_TagsTheAiSourceActivityWithThePromptIdentity()
    {
        using var listener = Listen(AiUsageMeter.MeterName);
        using var source = new ActivitySource(AiUsageMeter.MeterName);
        using var inner = new StubChatClient();
        using var client = new PromptTaggingChatClient(inner);
        var contract = new PromptContract("session-scoring", "3", "any-model", "be terse");

        using var activity = source.StartActivity("chat");
        activity.Should().NotBeNull();
        await client.GetResponseAsync(Prompt, contract.ToChatOptions(), TestContext.Current.CancellationToken);

        activity!.GetTagItem(PromptContract.NamePropertyKey).Should().Be("session-scoring");
        activity.GetTagItem(PromptContract.VersionPropertyKey).Should().Be("3");
        activity.GetTagItem(PromptContract.HashPropertyKey).Should().Be(contract.Hash);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_TagsTheSameWay()
    {
        using var listener = Listen(AiUsageMeter.MeterName);
        using var source = new ActivitySource(AiUsageMeter.MeterName);
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        using var client = new PromptTaggingChatClient(inner);
        var contract = new PromptContract("session-scoring", "3", "any-model", "be terse");

        using var activity = source.StartActivity("chat");
        await foreach (var update in client.GetStreamingResponseAsync(Prompt, contract.ToChatOptions(), TestContext.Current.CancellationToken))
        {
            update.Should().NotBeNull();
        }

        activity!.GetTagItem(PromptContract.VersionPropertyKey).Should().Be("3");
    }

    [Fact]
    public async Task GetResponseAsync_LeavesAnActivityFromAnotherSourceAlone()
    {
        using var listener = Listen("Some.Other.Source");
        using var source = new ActivitySource("Some.Other.Source");
        using var inner = new StubChatClient();
        using var client = new PromptTaggingChatClient(inner);

        using var activity = source.StartActivity("http");
        activity.Should().NotBeNull();
        await client.GetResponseAsync(
            Prompt,
            new PromptContract("session-scoring", "3", "any-model", "be terse").ToChatOptions(),
            TestContext.Current.CancellationToken);

        activity!.GetTagItem(PromptContract.NamePropertyKey).Should().BeNull(
            because: "only the pipeline's own gen_ai span carries the prompt identity");
    }

    [Fact]
    public async Task GetResponseAsync_WithNoContract_WritesNothing()
    {
        using var listener = Listen(AiUsageMeter.MeterName);
        using var source = new ActivitySource(AiUsageMeter.MeterName);
        using var inner = new StubChatClient();
        using var client = new PromptTaggingChatClient(inner);

        using var activity = source.StartActivity("chat");
        await client.GetResponseAsync(Prompt, new ChatOptions(), TestContext.Current.CancellationToken);

        activity!.TagObjects.Should().BeEmpty();
    }

    [Fact]
    public async Task GetResponseAsync_WithNoActivity_StillCallsThrough()
    {
        using var inner = new StubChatClient();
        using var client = new PromptTaggingChatClient(inner);

        await client.GetResponseAsync(
            Prompt,
            new PromptContract("session-scoring", "3", "any-model", "be terse").ToChatOptions(),
            TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1);
    }

    private static ActivityListener Listen(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
