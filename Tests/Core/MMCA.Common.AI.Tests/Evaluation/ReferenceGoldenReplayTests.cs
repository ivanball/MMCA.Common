using Microsoft.Extensions.AI;
using MMCA.Common.AI.Testing;

namespace MMCA.Common.AI.Tests.Evaluation;

/// <summary>
/// The framework's own golden-replay gate. A tiny reference summarizer is driven through the
/// harness against three recorded answers, so the shipped
/// <see cref="GoldenReplayTestsBase"/> is exercised on every pull request here rather than only in
/// the repos that consume it. No model, no credential and no network are involved.
/// <para>
/// The expected answer is read back from the SAME recorded file the harness replays, which is the
/// point of a golden corpus: the recording is the expectation, and re-recording it is a reviewable
/// diff rather than an edit to an assertion.
/// </para>
/// <para>
/// Each case carries the hash of the contract it was recorded against as a literal, never as
/// <c>ReferencePrompts.Contract.Hash</c>: a computed value would always equal the current one and
/// the hash gate would pass vacuously. Editing the reference prompt therefore fails this class until
/// the cases are re-recorded and the literal moved, which is the protocol the base enforces.
/// </para>
/// </summary>
public sealed class ReferenceGoldenReplayTests : GoldenReplayTestsBase
{
    /// <summary>The hash of <c>reference-summarize@1</c>, the contract these cases were recorded against.</summary>
    private const string RecordedAgainst = "5c7c2605ba80e5b17e814078868f210e31f3674e812af61c1df9504569bb8d06";

    /// <inheritdoc />
    protected override IEnumerable<GoldenReplayCase> Cases =>
    [
        new("plain-text", "a one-line answer", "Evaluation/Golden/case-plain-text.json", RecordedAgainst),
        new("with-usage", "an answer whose recording carries both token counts", "Evaluation/Golden/case-with-usage.json", RecordedAgainst),
        new("multiline", "an answer spanning several lines", "Evaluation/Golden/case-multiline.json", RecordedAgainst),
    ];

    /// <inheritdoc />
    protected override PromptContract CurrentContract => ReferencePrompts.Contract;

    /// <inheritdoc />
    protected override async Task AssertCaseAsync(
        GoldenReplayCase testCase,
        ReplayChatClient replay,
        CancellationToken cancellationToken)
    {
        var recorded = RecordedResponses.Read(Path.Combine(AppContext.BaseDirectory, testCase.ResponsePath));

        var answer = await SummarizeAsync(replay, InputFor(testCase.Id), cancellationToken);

        answer.Should().Be(recorded.Text, "the replayed answer is the recorded one, verbatim");
        recorded.Usage.Should().NotBeNull("a recorded answer carries the usage the run was measured by");
        recorded.Usage.InputTokenCount.Should().NotBeNull();
        recorded.Usage.OutputTokenCount.Should().NotBeNull();

        replay.CallCount.Should().Be(1, "the summarizer makes exactly one model call per case");
        replay.LastMessages.Should().ContainSingle("the input is sent as one user message");
        replay.LastOptions.Should().NotBeNull();
        replay.LastOptions.ModelId.Should().Be(ReferencePrompts.Model, "the request carries the pinned model");
        replay.LastOptions.Instructions.Should().Be(ReferencePrompts.SystemPrompt);
        PromptContract.ReadName(replay.LastOptions).Should().Be(ReferencePrompts.Name);
        PromptContract.ReadVersion(replay.LastOptions).Should().Be(ReferencePrompts.Version);
    }

    /// <summary>
    /// The reference "summarizer": the smallest piece of real code a golden case can pin. It sends
    /// the case input through the chat client under the reference contract and returns the answer
    /// text, which is exactly the shape a consumer's service has around its own prompt.
    /// </summary>
    private static async Task<string> SummarizeAsync(
        IChatClient client,
        string input,
        CancellationToken cancellationToken)
    {
        var response = await client.GetResponseAsync(
            input,
            ReferencePrompts.ForRequest(),
            cancellationToken);

        return response.Text;
    }

    private static string InputFor(string caseId) => caseId switch
    {
        "plain-text" => "The outbox pattern writes messages in the same transaction as the aggregate.",
        "with-usage" => "Token usage is recorded per call and tagged by prompt name, version and hash.",
        "multiline" => "A golden corpus is recorded once and replayed on every pull request.",
        _ => throw new InvalidOperationException($"No reference input for case '{caseId}'."),
    };
}
