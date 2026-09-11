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

    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;

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

        var tags = new TagList
        {
            { "model", model ?? "unknown" },
            { "prompt_name", promptName ?? "unknown" },
            { "prompt_version", promptVersion ?? "unknown" },
            { "provider", provider.ToString() },
        };

        if (usage.InputTokenCount is { } input)
        {
            _inputTokens.Add(input, tags);
        }

        if (usage.OutputTokenCount is { } output)
        {
            _outputTokens.Add(output, tags);
        }
    }
}
