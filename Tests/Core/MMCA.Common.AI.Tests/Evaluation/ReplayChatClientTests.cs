using Microsoft.Extensions.AI;
using MMCA.Common.AI.Testing;

namespace MMCA.Common.AI.Tests.Evaluation;

/// <summary>
/// The harness has to be trustworthy before anything it gates is: a replay that quietly answered
/// with the wrong recording, or a corpus gate that passed on an empty corpus, would turn the
/// evaluation into a green light that means nothing. This file pins the replay client and the
/// golden base that drives it; <see cref="RecordedResponsesTests"/> pins the file formats and the
/// prompt pin.
/// </summary>
public sealed class ReplayChatClientTests
{
    private const string Prompt = "Summarize this.";

    private static readonly PromptContract HarnessContract =
        new("harness-prompt", "1", "reference-model", "You answer harness questions.");

    [Fact]
    public async Task Replays_TheRecordedResponse()
    {
        using var client = new ReplayChatClient(Recorded("Recorded answer."));

        var response = await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        response.Text.Should().Be("Recorded answer.");
    }

    [Fact]
    public async Task Records_WhatTheCallerSent()
    {
        using var client = new ReplayChatClient(Recorded("Recorded answer."));
        var options = new ChatOptions { ModelId = "reference-model" };

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        client.CallCount.Should().Be(1);
        client.LastMessages.Should().ContainSingle();
        client.LastMessages[0].Text.Should().Be(Prompt);
        client.LastOptions.Should().BeSameAs(options);
    }

    [Fact]
    public async Task ConcurrentCalls_AreAllCounted()
    {
        using var client = new ReplayChatClient([Recorded("first"), Recorded("second")]);
        var cancellationToken = TestContext.Current.CancellationToken;

        // 8 workers x 2,000 calls: an unsynchronized read-then-increment loses counts under this load.
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                for (var i = 0; i < 2_000; i++)
                {
                    await client.GetResponseAsync(Prompt, options: null, cancellationToken);
                }
            },
            cancellationToken));
        await Task.WhenAll(workers);

        client.CallCount.Should().Be(16_000);
    }

    [Fact]
    public async Task Serves_RecordedResponsesInOrder_AndRepeatsTheLast()
    {
        using var client = new ReplayChatClient([Recorded("first"), Recorded("second")]);

        var first = await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);
        var second = await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);
        var third = await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        first.Text.Should().Be("first");
        second.Text.Should().Be("second");
        third.Text.Should().Be("second", "the last recording repeats rather than the harness throwing");
        client.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Streaming_YieldsTheRecordedTextAndTheRecordedUsage()
    {
        using var client = new ReplayChatClient(Recorded("Streamed answer."));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            Prompt, options: null, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        string.Concat(updates.Select(update => update.Text)).Should().Be("Streamed answer.");
        updates.SelectMany(update => update.Contents).OfType<UsageContent>().Should().ContainSingle(
            "a recorded usage has to reach the streaming path too, or a usage assertion would pass vacuously");
        client.CallCount.Should().Be(1);
    }

    [Fact]
    public void GetService_AnswersForItself_AndNullForAnythingElse()
    {
        using var client = new ReplayChatClient(Recorded("answer"));

        client.GetService(typeof(ReplayChatClient)).Should().BeSameAs(client);
        client.GetService(typeof(ChatClientMetadata)).Should().BeNull();
    }

    [Fact]
    public void Rejects_AnEmptyRecording()
    {
        Action act = () =>
        {
            using var client = new ReplayChatClient([]);
        };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task GoldenBase_Fails_WhenTheCorpusIsEmpty()
    {
        var harness = new EmptyCorpusHarness();

        Func<Task> act = harness.EveryGoldenCase_ReplaysAsRecorded;

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*corpus is empty*", "an evaluation that evaluates nothing must not pass");
    }

    [Fact]
    public async Task GoldenBase_Reports_EveryFailingCaseId()
    {
        var harness = new MissingRecordingHarness();

        Func<Task> act = harness.EveryGoldenCase_ReplaysAsRecorded;

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*first-case*");
        assertion.WithMessage("*second-case*", "the report names every failing case, not only the first");
    }

    [Fact]
    public void GoldenBase_HashGate_Fails_WhenTheCorpusIsEmpty()
    {
        var harness = new EmptyCorpusHarness();

        Action act = harness.EveryGoldenCase_WasRecordedAgainstTheCurrentContract;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*corpus is empty*", "the hash gate refuses an empty corpus exactly as the replay gate does");
    }

    [Fact]
    public void GoldenBase_HashGate_Fails_NamingEveryCaseRecordedAgainstAStaleContract()
    {
        var harness = new StaleHashHarness();

        Action act = harness.EveryGoldenCase_WasRecordedAgainstTheCurrentContract;

        var assertion = act.Should().Throw<InvalidOperationException>();
        assertion.WithMessage("*stale-one*");
        assertion.WithMessage("*stale-two*", "the report names every stale case, not only the first");
        assertion.WithMessage("*re-record*", "the message tells the author what to do");
        assertion.WithMessage($"*{HarnessContract.Hash}*", "the message names the hash to record against");
        assertion.Which.Message.Should().NotContain(
            "current-one",
            "a case recorded against the current contract is not reported");
    }

    [Fact]
    public void GoldenBase_HashGate_Passes_WhenEveryCaseMatchesTheCurrentContract()
    {
        var harness = new MissingRecordingHarness();

        Action act = harness.EveryGoldenCase_WasRecordedAgainstTheCurrentContract;

        act.Should().NotThrow("both cases were recorded against the harness contract");
    }

    [Fact]
    public void GoldenBase_HashGate_Fails_WhenOnlyTheSystemPromptMoved()
    {
        var harness = new StaleHashHarness(HarnessContract with { SystemPrompt = "Answer in French." });

        Action act = harness.EveryGoldenCase_WasRecordedAgainstTheCurrentContract;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*current-one*", "a system-prompt edit moves the hash, so even the once-current case is stale");
    }

    private static ChatResponse Recorded(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 },
        };

    /// <summary>A corpus with no cases: the gate must refuse it rather than pass.</summary>
    private sealed class EmptyCorpusHarness : GoldenReplayTestsBase
    {
        protected override IEnumerable<GoldenReplayCase> Cases => [];

        protected override PromptContract CurrentContract => HarnessContract;

        protected override Task AssertCaseAsync(
            GoldenReplayCase testCase,
            ReplayChatClient replay,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Two cases whose recordings are not on disk, so both fail and both are reported.</summary>
    private sealed class MissingRecordingHarness : GoldenReplayTestsBase
    {
        protected override IEnumerable<GoldenReplayCase> Cases =>
        [
            new("first-case", "a recording that is not there", "Evaluation/Golden/does-not-exist-1.json", HarnessContract.Hash),
            new("second-case", "another recording that is not there", "Evaluation/Golden/does-not-exist-2.json", HarnessContract.Hash),
        ];

        protected override PromptContract CurrentContract => HarnessContract;

        protected override Task AssertCaseAsync(
            GoldenReplayCase testCase,
            ReplayChatClient replay,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// One case recorded against the harness contract and two recorded against an older one, so the
    /// hash gate must name exactly the two stale ones.
    /// </summary>
    private sealed class StaleHashHarness(PromptContract? current = null) : GoldenReplayTestsBase
    {
        private static readonly string OlderHash = (HarnessContract with { Version = "0" }).Hash;

        protected override IEnumerable<GoldenReplayCase> Cases =>
        [
            new("current-one", "recorded against the current contract", "Evaluation/Golden/case-plain-text.json", HarnessContract.Hash),
            new("stale-one", "recorded against an older contract", "Evaluation/Golden/case-plain-text.json", OlderHash),
            new("stale-two", "also recorded against an older contract", "Evaluation/Golden/case-multiline.json", OlderHash),
        ];

        protected override PromptContract CurrentContract => current ?? HarnessContract;

        protected override Task AssertCaseAsync(
            GoldenReplayCase testCase,
            ReplayChatClient replay,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
