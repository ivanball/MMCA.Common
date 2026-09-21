using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// The streaming path used to inspect the request and then get out of the way. It now inspects every
/// update before it is yielded, which is the only place a block can still be a control rather than a
/// statement: once the caller holds the fragment, refusing it changes nothing.
/// </summary>
public sealed class StreamedGuardrailTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new(ChatRole.User, "score this")];

    [Fact]
    public async Task EveryUpdate_IsInspectedBeforeItIsYielded()
    {
        var guardrail = new StubGuardrail();
        using var inner = new StubChatClient(updates:
        [
            new ChatResponseUpdate(ChatRole.Assistant, "one"),
            new ChatResponseUpdate(ChatRole.Assistant, "two"),
        ]);
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var seen = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken))
        {
            seen.Add(update.Text);
        }

        seen.Should().Equal("one", "two");
        guardrail.UpdateInspections.Should().Be(2);
        guardrail.RequestInspections.Should().Be(1);
    }

    [Fact]
    // A block mid-stream ends the stream there. The caller keeps what it already received, which is
    // the honest contract: those fragments were allowed when they went past.
    public async Task ABlockedUpdate_EndsTheStreamAndKeepsWhatCameBefore()
    {
        var guardrail = new StubGuardrail(updateVerdict: update =>
            update.Text == "srv-01.internal"
                ? GuardrailVerdict.Block("leaked an internal hostname")
                : GuardrailVerdict.Allow);
        using var inner = new StubChatClient(updates:
        [
            new ChatResponseUpdate(ChatRole.Assistant, "the host is "),
            new ChatResponseUpdate(ChatRole.Assistant, "srv-01.internal"),
            new ChatResponseUpdate(ChatRole.Assistant, " and it is up"),
        ]);
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var seen = new List<string>();
        var act = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken))
            {
                seen.Add(update.Text);
            }
        };

        (await act.Should().ThrowAsync<ChatGuardrailException>())
            .WithMessage("leaked an internal hostname");
        seen.Should().Equal("the host is ");
        guardrail.UpdateInspections.Should().Be(2, "the third update is never reached");
    }

    [Fact]
    // A guardrail written before the member existed keeps compiling and keeps allowing, which is what
    // the default interface member buys.
    public async Task AGuardrailThatDoesNotOverrideTheMember_AllowsEveryUpdate()
    {
        var guardrail = new PreStreamingGuardrail();
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var seen = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken))
        {
            seen.Add(update.Text);
        }

        seen.Should().Equal("ok");
    }

    private sealed class PreStreamingGuardrail : IChatGuardrail
    {
        public ValueTask<GuardrailVerdict> InspectRequestAsync(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);

        public ValueTask<GuardrailVerdict> InspectResponseAsync(
            ChatResponse response,
            ChatOptions? options,
            CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);
    }
}
