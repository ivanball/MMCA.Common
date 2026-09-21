using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Guardrails;

namespace MMCA.Common.AI.Chat;

/// <summary>
/// The outermost layer of the governed chat pipeline: the one that decides what a call is ALLOWED to
/// do, before any other layer gets to log it, cache it or count it.
/// <para>
/// Four bounds, all read from <see cref="AiSettings"/> and all applied to both the buffered and the
/// streaming path:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Output tokens.</b> <c>MaxOutputTokens</c> is clamped down to
/// <see cref="AiSettings.MaxOutputTokens"/>. A caller asking for less keeps its smaller number, a
/// caller asking for more (or for nothing at all) gets the configured ceiling. This is a cost bound:
/// output tokens are the expensive half of a chat call.
/// </description></item>
/// <item><description>
/// <b>Wall clock.</b> The call runs under a token linked to the caller's own and cancelled after
/// <see cref="AiSettings.Timeout"/>, so a provider that stops answering cannot hold a request thread
/// indefinitely and a caller that cancels early still wins.
/// </description></item>
/// <item><description>
/// <b>Tool use.</b> While <see cref="AiSettings.AllowTools"/> is false, tools and the tool mode are
/// stripped from the request. A model that is never handed a tool cannot be argued into calling one,
/// which is the difference between "we told it not to" and "it could not". While it is true, each
/// tool is offered only when EVERY registered <see cref="Guardrails.IChatToolPolicy"/> allows it,
/// and a tool marked consequential
/// (<see cref="Guardrails.ChatToolPolicy.ConsequentialPropertyKey"/>) only when the request also
/// names it in <see cref="Guardrails.ChatToolPolicy.ConfirmedToolsPropertyKey"/>. With no policy
/// registered every tool is stripped: the layer fails closed, so a missing policy costs a capability
/// rather than granting one.
/// </description></item>
/// <item><description>
/// <b>Input size.</b> When <see cref="AiSettings.PerCallInputTokenBudget"/> is set, an ESTIMATED
/// input size above the budget fails the call locally instead of paying for it remotely.
/// </description></item>
/// <item><description>
/// <b>Model.</b> The request goes out naming <see cref="AiSettings.Model"/>, and a request that
/// names a different model is refused. Adapters differ in whether a per-request model id overrides
/// the one the client was built with; pinning it here makes <see cref="PromptContract.Model"/>
/// mean the same thing on every provider: the model the prompt was evaluated against, and the
/// only one it may be sent to.
/// </description></item>
/// </list>
/// <para>
/// <b>The input estimate is an estimate.</b> It asks the inner pipeline for an
/// <see cref="IAiTokenEstimator"/> and falls back to one token per four characters, the usual rough
/// rule for English prose. It reads only the text of the messages plus the instructions, so images,
/// tool schemas, provider-side system additions and non-Latin scripts are all under-counted. Use it
/// as a runaway-input guardrail with headroom, never as a billing figure: the authoritative counts
/// are the provider's, recorded after the fact by <see cref="UsageRecordingChatClient"/>.
/// </para>
/// <para>
/// The options a caller passes are never mutated: each call works on a clone, so a caller reusing
/// one <see cref="ChatOptions"/> instance across requests does not silently inherit this client's
/// clamping.
/// </para>
/// </summary>
public sealed class BoundedChatClient : DelegatingChatClient
{
    private readonly AiSettings _settings;
    private readonly IChatToolPolicy[] _toolPolicies;

    /// <summary>Initializes a new instance of the <see cref="BoundedChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="settings">The bounds to enforce.</param>
    /// <remarks>
    /// No tool policies, which while <see cref="AiSettings.AllowTools"/> is true means every tool is
    /// stripped. Registration refuses that combination outright, so this overload is for a host that
    /// composes the client by hand and does not offer tools.
    /// </remarks>
    public BoundedChatClient(IChatClient innerClient, AiSettings settings)
        : this(innerClient, settings, [])
    {
    }

    /// <summary>Initializes a new instance of the <see cref="BoundedChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="settings">The bounds to enforce.</param>
    /// <param name="toolPolicies">Every tool policy to consult, all of which must allow a tool for it to be offered.</param>
    public BoundedChatClient(
        IChatClient innerClient,
        AiSettings settings,
        IEnumerable<IChatToolPolicy> toolPolicies)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(toolPolicies);
        _settings = settings;
        _toolPolicies = [.. toolPolicies];
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var bounded = Bound(options);
        EnforceInputBudget(materialized, bounded);

        using var timeout = CreateLinkedTimeout(cancellationToken);

        return await base.GetResponseAsync(materialized, bounded, timeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Bound and budget-check eagerly, outside the iterator: a request that the configuration
        // forbids is refused when it is made, not when somebody starts reading the stream.
        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var bounded = Bound(options);
        EnforceInputBudget(materialized, bounded);

        return StreamBoundedAsync(materialized, bounded, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamBoundedAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CreateLinkedTimeout(cancellationToken);

        // The timeout covers the whole stream, not just its first update: a provider that opens a
        // response and then stalls is the failure mode a per-call budget exists to bound.
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, timeout.Token).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Estimates the input size of a request, in tokens, the way the budget check does.
    /// </summary>
    /// <param name="messages">The messages about to be sent.</param>
    /// <param name="options">The options about to be sent, whose instructions count too.</param>
    /// <param name="estimator">An estimator, or <see langword="null"/> for the character heuristic.</param>
    /// <returns>The estimated input token count.</returns>
    public static int EstimateInputTokens(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IAiTokenEstimator? estimator)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            builder.Append(options.Instructions);
        }

        foreach (var message in messages)
        {
            builder.Append(message.Text);
        }

        var text = builder.ToString();

        // Four characters per token: the usual rough rule for English prose, and deliberately the
        // cheaper-to-be-wrong direction (an under-count lets a call through, it never blocks one
        // that would have fit).
        return estimator is null ? (text.Length + 3) / 4 : estimator.EstimateTokenCount(text);
    }

    private ChatOptions Bound(ChatOptions? options)
    {
        var bounded = options?.Clone() ?? new ChatOptions();

        bounded.MaxOutputTokens = bounded.MaxOutputTokens is { } requested
            ? Math.Min(requested, _settings.MaxOutputTokens)
            : _settings.MaxOutputTokens;

        if (!_settings.AllowTools)
        {
            bounded.Tools = null;
            bounded.ToolMode = null;
        }
        else
        {
            bounded.Tools = FilterTools(bounded);
            if (bounded.Tools is null)
            {
                // A tool mode without tools is a request the provider cannot honor, and
                // RequireAny would make it an error rather than a plain answer.
                bounded.ToolMode = null;
            }
        }

        if (_settings.Model is { } pinned)
        {
            if (bounded.ModelId is { } requestedModel && !string.Equals(requestedModel, pinned, StringComparison.Ordinal))
            {
                var setting = $"{AiSettings.SectionName}:{nameof(AiSettings.Model)}";
                throw new InvalidOperationException(
                    $"The request names model '{requestedModel}' but {setting} pins '{pinned}'. The model is part of the "
                    + "prompt contract and is not negotiated per call: align the PromptContract.Model with the "
                    + "configured model, or change the configured model deliberately and re-evaluate.");
            }

            // Stated explicitly so every adapter is asked for the pinned model by name, whether or
            // not it honors a per-request override, and so the model tag on an errored call is
            // never blank.
            bounded.ModelId = pinned;
        }

        return bounded;
    }

    /// <summary>
    /// Narrows the tools on a request to the ones every policy allows and the caller confirmed where
    /// confirmation is required.
    /// </summary>
    /// <param name="bounded">This client's own clone of the options, never the caller's.</param>
    /// <returns>The tools to offer, or <see langword="null"/> when none survive.</returns>
    private List<AITool>? FilterTools(ChatOptions bounded)
    {
        if (bounded.Tools is not { Count: > 0 } requested)
        {
            return null;
        }

        // No policy is not "no restriction": an unanswered question about a capability is answered
        // by withholding it. Registration refuses this combination, so reaching here means the
        // client was composed by hand.
        if (_toolPolicies.Length == 0)
        {
            return null;
        }

        var confirmed = ChatToolPolicy.ReadConfirmedTools(bounded);
        var offered = new List<AITool>(requested.Count);

        foreach (var tool in requested)
        {
            if (!Array.TrueForAll(_toolPolicies, policy => policy.Authorize(tool, bounded) == ToolAuthorization.Allowed))
            {
                continue;
            }

            // The confirmation is checked on top of the policies, never instead of them: a confirmed
            // tool a policy denies stays denied, because a human saying yes to a prompt is not a
            // grant of an authority the application never had.
            if (ChatToolPolicy.IsConsequential(tool) && !confirmed.Contains(tool.Name, StringComparer.Ordinal))
            {
                continue;
            }

            offered.Add(tool);
        }

        return offered.Count == 0 ? null : offered;
    }

    private CancellationTokenSource CreateLinkedTimeout(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_settings.Timeout);
        return linked;
    }

    private void EnforceInputBudget(IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        if (_settings.PerCallInputTokenBudget is not { } budget)
        {
            return;
        }

        var estimated = EstimateInputTokens(messages, options, this.GetService<IAiTokenEstimator>());
        if (estimated <= budget)
        {
            return;
        }

        var setting = $"{AiSettings.SectionName}:{nameof(AiSettings.PerCallInputTokenBudget)}";
        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The request's estimated input size ({estimated} tokens) exceeds {setting} ({budget} tokens). The estimate is approximate: shorten the prompt or raise the budget deliberately."));
    }
}
