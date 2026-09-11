using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Tests.Fixtures;

/// <summary>
/// A recording, offline stand-in for a provider client. Every test in this project drives the
/// governance pipeline through one of these: nothing here ever reaches the Anthropic API, and the
/// package carries no credential a test could accidentally use.
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _respond;
    private readonly IReadOnlyList<ChatResponseUpdate> _updates;
    private readonly object? _service;

    public StubChatClient(
        ChatResponse? response = null,
        IReadOnlyList<ChatResponseUpdate>? updates = null,
        Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? respond = null,
        object? service = null)
    {
        var fixedResponse = response ?? new ChatResponse();
        _respond = respond ?? ((_, _, _) => Task.FromResult(fixedResponse));
        _updates = updates ?? [];
        _service = service;
    }

    /// <summary>The options the innermost client was actually called with.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>The messages the innermost client was actually called with.</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>How many times the innermost client was called, on either path.</summary>
    public int CallCount { get; private set; }

    public void Dispose()
    {
        // Nothing to release: the stub holds no transport.
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastMessages = [.. messages];
        LastOptions = options;
        CallCount++;

        return _respond(LastMessages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastMessages = [.. messages];
        LastOptions = options;
        CallCount++;

        foreach (var update in _updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>A token estimator that answers a fixed count, so a budget test is deterministic.</summary>
internal sealed class FixedTokenEstimator(int tokens) : Chat.IAiTokenEstimator
{
    public int EstimateTokenCount(string text) => tokens;
}
