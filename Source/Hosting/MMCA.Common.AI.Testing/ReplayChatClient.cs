using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Testing;

/// <summary>
/// An offline <see cref="IChatClient"/> that answers with responses recorded earlier. It is the
/// half of the evaluation harness that removes the model from the loop: a golden case replays
/// through the real code under test, so a prompt edit, a pipeline change or a parsing regression
/// fails a normal CI leg instead of waiting for a live run that costs money and is not repeatable.
/// <para>
/// Nothing here reaches a network and nothing here needs a credential. What goes OUT is recorded
/// too (<see cref="LastMessages"/>, <see cref="LastOptions"/>, <see cref="CallCount"/>), so a test
/// can assert the request as well as the answer.
/// </para>
/// </summary>
public sealed class ReplayChatClient : IChatClient
{
    private readonly IReadOnlyList<ChatResponse> _responses;

    /// <summary>
    /// Creates a client that answers every call with the one recorded response.
    /// </summary>
    /// <param name="response">The recorded response to replay.</param>
    public ReplayChatClient(ChatResponse response)
        : this([response ?? throw new ArgumentNullException(nameof(response))])
    {
    }

    /// <summary>
    /// Creates a client that answers with the recorded responses in order. The last one repeats, so
    /// a test that calls once more than it recorded gets a stable answer rather than an exception
    /// from the harness pretending to be a failure in the code under test.
    /// </summary>
    /// <param name="responses">The recorded responses, in the order they are to be served.</param>
    public ReplayChatClient(IReadOnlyList<ChatResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);

        if (responses.Count == 0)
        {
            throw new ArgumentException("A replay client needs at least one recorded response.", nameof(responses));
        }

        _responses = [.. responses];
    }

    /// <summary>Gets the options the client was last called with.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>Gets the messages the client was last called with.</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>Gets how many times the client was called, on either path.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: the replay client holds no transport.
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Record(messages, options));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = Record(messages, options);

        // The same recorded response, projected onto the streaming shape by the abstraction's own
        // conversion: one update per recorded message plus a final update carrying the usage when
        // the recording has one. A test asserting the streamed answer is therefore asserting the
        // same corpus as the buffered path, not a second hand-built approximation of it.
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    private ChatResponse Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        LastMessages = [.. messages];
        LastOptions = options;

        var index = Math.Min(CallCount, _responses.Count - 1);
        CallCount++;

        return _responses[index];
    }
}
