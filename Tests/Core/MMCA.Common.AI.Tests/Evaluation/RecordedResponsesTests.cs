using Microsoft.Extensions.AI;
using MMCA.Common.AI.Testing;

namespace MMCA.Common.AI.Tests.Evaluation;

/// <summary>
/// The two files a corpus is made of: the recorded response and the prompt-version pin. Both are
/// read from disk at run time, so both are pinned here, including the exact failure messages the
/// pin base reports. A protocol whose failure message does not say what to do is a protocol nobody
/// follows.
/// </summary>
public sealed class RecordedResponsesTests
{
    [Fact]
    public void RoundTrips_AResponse_ThroughJson()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "case-round-trip.json");
            var original = new ChatResponse(new ChatMessage(ChatRole.Assistant, "One sentence."))
            {
                ModelId = "reference-model",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 42, OutputTokenCount = 9, TotalTokenCount = 51 },
            };

            RecordedResponses.Write(path, original);
            var read = RecordedResponses.Read(path);

            read.Text.Should().Be("One sentence.");
            read.ModelId.Should().Be("reference-model");
            read.FinishReason.Should().Be(ChatFinishReason.Stop);
            read.Usage.Should().NotBeNull();
            read.Usage!.InputTokenCount.Should().Be(42);
            read.Usage.OutputTokenCount.Should().Be(9);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Serializes_InTheAbstractionShape_NotAVendorWireFormat()
    {
        var json = RecordedResponses.Serialize(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "One sentence."))
            {
                Usage = new UsageDetails { InputTokenCount = 42, OutputTokenCount = 9 },
            });

        // The recording is a ChatResponse through the abstraction's own serializer options, so the
        // same corpus replays unchanged after a provider swap. A vendor envelope would not.
        json.Should().Contain("\"messages\"");
        json.Should().NotContain("stop_reason", "a provider wire format would tie the corpus to one adapter");
        json.Should().NotContain("input_tokens", "the usage is recorded in the abstraction's shape");
        json.Split('\n').Length.Should().BeGreaterThan(
            1,
            "the recording is pretty-printed so a re-recording is a reviewable diff");

        RecordedResponses.Deserialize(json).Text.Should().Be("One sentence.");
    }

    [Fact]
    public void Read_NamesThePath_WhenTheRecordingIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmca-no-such-recording.json");

        var act = () => RecordedResponses.Read(path);

        act.Should().Throw<FileNotFoundException>().WithMessage($"*{path}*");
    }

    [Fact]
    public void Deserialize_Rejects_BlankJson()
    {
        var act = () => RecordedResponses.Deserialize("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PinBase_NamesTheExactLineToAdd_WhenAContractIsNotRecorded()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "prompt-versions.json");
            File.WriteAllText(path, "{}");
            var contract = DemoContract();
            var harness = new PinHarness(path, contract);

            Action act = harness.EveryContract_HasARecordedHash;

            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*\"harness-demo@1\": \"{contract.Hash}\"*",
                    "the failure has to carry the line to paste, or the protocol is a guess");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PinBase_SaysBumpOrRevert_WhenTheRecordedHashDrifted()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "prompt-versions.json");
            File.WriteAllText(path, "{ \"harness-demo@1\": \"" + new string('0', 64) + "\" }");
            var harness = new PinHarness(path, DemoContract());

            Action act = harness.EveryRecordedHash_MatchesTheCurrentContract;

            act.Should().Throw<InvalidOperationException>()
                .WithMessage(
                    "*the prompt, model or system text changed without a version bump: bump Version and record the new hash, or revert*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PinBase_Accepts_OlderVersionsLeftInTheFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var contract = DemoContract();
            var path = Path.Combine(directory.FullName, "prompt-versions.json");
            File.WriteAllText(
                path,
                "{ \"harness-demo@0\": \"" + new string('0', 64) + "\", \"harness-demo@1\": \"" + contract.Hash + "\" }");
            var harness = new PinHarness(path, contract);

            Action recorded = harness.EveryContract_HasARecordedHash;
            Action matches = harness.EveryRecordedHash_MatchesTheCurrentContract;

            recorded.Should().NotThrow();
            matches.Should().NotThrow("a retired version stays in the file as history");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PinBase_Fails_WhenNoContractsAreDeclared()
    {
        var harness = new PinHarness(Path.Combine(Path.GetTempPath(), "mmca-empty-pin.json"));

        Action recorded = harness.EveryContract_HasARecordedHash;
        Action matches = harness.EveryRecordedHash_MatchesTheCurrentContract;

        recorded.Should().Throw<InvalidOperationException>().WithMessage("*No prompt contracts are declared*");
        matches.Should().Throw<InvalidOperationException>().WithMessage("*No prompt contracts are declared*");
    }

    private static PromptContract DemoContract() =>
        new("harness-demo", "1", "harness-model", "Answer in one sentence.");

    /// <summary>The pin base over a temp file, so its failure messages can be asserted directly.</summary>
    private sealed class PinHarness(string pinFilePath, params PromptContract[] contracts) : PromptContractPinTestsBase
    {
        protected override IEnumerable<PromptContract> Contracts => contracts;

        protected override string PinFilePath => pinFilePath;
    }
}
