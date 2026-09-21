using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;

namespace MMCA.Common.AI.Tests.Fixtures;

/// <summary>
/// A guardrail whose two verdicts are supplied by the test, so the pipeline's behavior on a block is
/// exercised without the package shipping a content policy of its own.
/// </summary>
internal sealed class StubGuardrail(
    GuardrailVerdict? requestVerdict = null,
    GuardrailVerdict? responseVerdict = null,
    Func<ChatResponseUpdate, GuardrailVerdict>? updateVerdict = null) : IChatGuardrail
{
    /// <summary>How many times the request inspection ran.</summary>
    public int RequestInspections { get; private set; }

    /// <summary>How many times the response inspection ran.</summary>
    public int ResponseInspections { get; private set; }

    /// <summary>How many streamed updates were inspected.</summary>
    public int UpdateInspections { get; private set; }

    public ValueTask<GuardrailVerdict> InspectRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        RequestInspections++;
        return ValueTask.FromResult(requestVerdict ?? GuardrailVerdict.Allow);
    }

    public ValueTask<GuardrailVerdict> InspectResponseAsync(
        ChatResponse response,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ResponseInspections++;
        return ValueTask.FromResult(responseVerdict ?? GuardrailVerdict.Allow);
    }

    public ValueTask<GuardrailVerdict> InspectStreamedUpdateAsync(
        ChatResponseUpdate update,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        UpdateInspections++;
        return ValueTask.FromResult(updateVerdict?.Invoke(update) ?? GuardrailVerdict.Allow);
    }
}
