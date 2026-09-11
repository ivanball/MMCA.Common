using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Observability;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// Usage is read back through a <see cref="MeterListener"/> scoped to this test's own
/// <see cref="IMeterFactory"/>, so a parallel test writing to the same meter NAME cannot be mistaken
/// for this one's measurements.
/// </summary>
public sealed class UsageRecordingChatClientTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new(ChatRole.User, "score this")];

    [Fact]
    public async Task GetResponseAsync_RecordsBothCountersWithThePromptTags()
    {
        using var recorder = new UsageRecorder();
        var response = new ChatResponse
        {
            ModelId = "claude-haiku-4-5",
            Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 34 },
        };
        using var inner = new StubChatClient(response);
        using var client = new UsageRecordingChatClient(inner, recorder.Meter, AiProvider.Anthropic);
        var options = new PromptContract("session-scoring", "3", "claude-haiku-4-5", "be terse").ToChatOptions();

        await client.GetResponseAsync(Prompt, options, TestContext.Current.CancellationToken);

        recorder.Measurements.Should().HaveCount(2);

        var input = recorder.Measurements.Single(m => m.Instrument == AiUsageMeter.InputTokensCounterName);
        input.Value.Should().Be(120);
        input.Tags["model"].Should().Be("claude-haiku-4-5");
        input.Tags["prompt_name"].Should().Be("session-scoring");
        input.Tags["prompt_version"].Should().Be("3");
        input.Tags["provider"].Should().Be("Anthropic");

        recorder.Measurements.Single(m => m.Instrument == AiUsageMeter.OutputTokensCounterName).Value.Should().Be(34);
    }

    [Fact]
    public async Task GetResponseAsync_RecordsNothingWhenTheProviderReportedNoUsage()
    {
        using var recorder = new UsageRecorder();
        using var inner = new StubChatClient(new ChatResponse { ModelId = "claude-haiku-4-5" });
        using var client = new UsageRecordingChatClient(inner, recorder.Meter, AiProvider.Anthropic);

        await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        recorder.Measurements.Should().BeEmpty(
            because: "an absent count must not read as a zero on a spend dashboard");
    }

    [Fact]
    public async Task GetResponseAsync_TagsUnstampedRequestsAsUnknown()
    {
        using var recorder = new UsageRecorder();
        var response = new ChatResponse { Usage = new UsageDetails { InputTokenCount = 10 } };
        using var inner = new StubChatClient(response);
        using var client = new UsageRecordingChatClient(inner, recorder.Meter, AiProvider.Anthropic);

        await client.GetResponseAsync(Prompt, new ChatOptions { ModelId = "fallback-model" }, TestContext.Current.CancellationToken);

        var measurement = recorder.Measurements.Should().ContainSingle().Subject;
        measurement.Tags["model"].Should().Be("fallback-model");
        measurement.Tags["prompt_name"].Should().Be("unknown");
        measurement.Tags["prompt_version"].Should().Be("unknown");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RecordsUsageDeliveredInTheStream()
    {
        using var recorder = new UsageRecorder();
        var usage = new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
        {
            InputTokenCount = 7,
            OutputTokenCount = 3,
        })])
        {
            ModelId = "claude-haiku-4-5",
        };
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok"), usage]);
        using var client = new UsageRecordingChatClient(inner, recorder.Meter, AiProvider.Anthropic);

        await foreach (var update in client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken))
        {
            // Drain the stream; the assertions are on what the meter saw.
            _ = update;
        }

        recorder.Measurements.Should().HaveCount(2);
        recorder.Measurements.Single(m => m.Instrument == AiUsageMeter.InputTokensCounterName).Value.Should().Be(7);
        recorder.Measurements.Single(m => m.Instrument == AiUsageMeter.OutputTokensCounterName).Value.Should().Be(3);
    }

    private sealed record Measurement(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>
    /// A meter factory plus a listener filtered to the meters THIS factory created, which is what
    /// makes the assertions safe under xUnit's parallel test classes.
    /// </summary>
    private sealed class UsageRecorder : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = [];
        private readonly Lock _gate = new();

        public UsageRecorder()
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = _provider.GetRequiredService<IMeterFactory>();
            Meter = new AiUsageMeter(factory);

            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter.Scope, factory)
                        && string.Equals(instrument.Meter.Name, AiUsageMeter.MeterName, StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copied[tag.Key] = tag.Value;
                }

                lock (_gate)
                {
                    _measurements.Add(new Measurement(instrument.Name, value, copied));
                }
            });

            _listener.Start();
        }

        public AiUsageMeter Meter { get; }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose()
        {
            _listener.Dispose();
            _provider.Dispose();
        }
    }
}
