using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Observability;

/// <summary>
/// The framework-wide token-usage meter: two counters that answer "what did the model cost, and
/// which prompt spent it".
/// <para>
/// Every app that adopts the governed client reports to the SAME meter name and the same two
/// counter names, so one dashboard query covers every service instead of each one inventing a
/// per-module meter. The dimensions carry the attribution: <c>model</c> and <c>provider</c> for the
/// cost side, <c>prompt_name</c> and <c>prompt_version</c> (stamped by
/// <see cref="PromptContract"/>) for the "which change moved this number" side, which is what turns
/// a spend graph into a per-prompt regression signal.
/// </para>
/// </summary>
public sealed class AiUsageMeter
{
    /// <summary>
    /// The meter name, shared with the <c>ActivitySource</c> the pipeline's OpenTelemetry layer
    /// publishes under, so a host enables traces and metrics for the model dependency with one name.
    /// </summary>
    public const string MeterName = "MMCA.Common.AI";

    /// <summary>Counter name for prompt (input) tokens.</summary>
    public const string InputTokensCounterName = "mmca.ai.input_tokens";

    /// <summary>Counter name for completion (output) tokens.</summary>
    public const string OutputTokensCounterName = "mmca.ai.output_tokens";

    /// <summary>
    /// Histogram name for end-to-end call latency, in seconds.
    /// <para>
    /// Microsoft.Extensions.AI's own <c>UseOpenTelemetry</c> layer already publishes
    /// <c>gen_ai.client.operation.duration</c> on this same meter, and this histogram does not
    /// replace it: the standard instrument carries the GenAI semantic-convention dimensions, while
    /// this one carries <c>prompt_name</c>, <c>prompt_version</c> and <c>outcome</c>, so a latency
    /// regression can be attributed to the prompt that caused it and a failure rate can be read off
    /// the same series as the latency.
    /// </para>
    /// </summary>
    public const string CallDurationHistogramName = "mmca.ai.call.duration";

    /// <summary>The <c>outcome</c> tag value for a call that returned an answer.</summary>
    public const string SuccessOutcome = "success";

    /// <summary>The <c>outcome</c> tag value for a call that threw.</summary>
    public const string ErrorOutcome = "error";

    /// <summary>The <c>outcome</c> tag value for a call the caller (or a bound) cancelled.</summary>
    public const string CanceledOutcome = "canceled";

    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Histogram<double> _callDuration;

    /// <summary>Initializes a new instance of the <see cref="AiUsageMeter"/> class.</summary>
    /// <param name="meterFactory">The factory that owns the meter's lifetime.</param>
    /// <remarks>
    /// The <see cref="Meter"/> is created through the factory and deliberately NOT retained or
    /// disposed here: the factory owns it, and disposing a factory-owned meter from a consumer
    /// silently kills the instrument for every other holder of the same name. This type is therefore
    /// not <see cref="IDisposable"/>, matching how the rest of the framework meters work.
    /// </remarks>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The meter is owned by the IMeterFactory, which disposes it with the container. Disposing a factory-owned meter here would silently kill the instrument for every other holder of the same name.")]
    public AiUsageMeter(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _inputTokens = meter.CreateCounter<long>(
            InputTokensCounterName,
            unit: "{token}",
            description: "Prompt tokens billed by the language-model provider.");
        _outputTokens = meter.CreateCounter<long>(
            OutputTokensCounterName,
            unit: "{token}",
            description: "Completion tokens billed by the language-model provider.");
        _callDuration = meter.CreateHistogram<double>(
            CallDurationHistogramName,
            unit: "s",
            description: "End-to-end duration of a governed chat call, tagged with the prompt identity and the outcome.");
    }

    /// <summary>
    /// Records one call's token usage. A <see langword="null"/> usage, or one whose counts the
    /// provider did not report, records nothing: an absent number must not read as a zero on a
    /// spend dashboard.
    /// </summary>
    /// <param name="usage">The provider-reported usage, possibly <see langword="null"/>.</param>
    /// <param name="model">The model that answered.</param>
    /// <param name="promptName">The prompt name stamped on the request, if any.</param>
    /// <param name="promptVersion">The prompt version stamped on the request, if any.</param>
    /// <param name="provider">The provider the call went to.</param>
    public void Record(
        UsageDetails? usage,
        string? model,
        string? promptName,
        string? promptVersion,
        AiProvider provider)
    {
        if (usage is null)
        {
            return;
        }

        var tags = AttributionTags(model, promptName, promptVersion, provider);

        if (usage.InputTokenCount is { } input)
        {
            _inputTokens.Add(input, tags);
        }

        if (usage.OutputTokenCount is { } output)
        {
            _outputTokens.Add(output, tags);
        }
    }

    /// <summary>
    /// Records one call's end-to-end duration, in seconds, under the same attribution dimensions as
    /// the token counters plus an <c>outcome</c>. Recorded on every call, including one that threw,
    /// so a failure rate and a latency distribution come off one series.
    /// </summary>
    /// <param name="elapsed">The measured wall-clock duration of the call.</param>
    /// <param name="model">The model that answered, when the call got far enough to know.</param>
    /// <param name="promptName">The prompt name stamped on the request, if any.</param>
    /// <param name="promptVersion">The prompt version stamped on the request, if any.</param>
    /// <param name="provider">The provider the call went to.</param>
    /// <param name="outcome">One of <see cref="SuccessOutcome"/>, <see cref="ErrorOutcome"/> or <see cref="CanceledOutcome"/>.</param>
    public void RecordDuration(
        TimeSpan elapsed,
        string? model,
        string? promptName,
        string? promptVersion,
        AiProvider provider,
        string outcome)
    {
        var tags = AttributionTags(model, promptName, promptVersion, provider);
        tags.Add("outcome", outcome ?? "unknown");

        _callDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>
    /// The attribution dimensions every instrument on this meter shares, so a dashboard can join the
    /// cost series and the latency series on the same tags.
    /// </summary>
    private static TagList AttributionTags(
        string? model,
        string? promptName,
        string? promptVersion,
        AiProvider provider) => new()
        {
            { "model", model ?? "unknown" },
            { "prompt_name", promptName ?? "unknown" },
            { "prompt_version", promptVersion ?? "unknown" },
            { "provider", provider.ToString() },
        };
}
