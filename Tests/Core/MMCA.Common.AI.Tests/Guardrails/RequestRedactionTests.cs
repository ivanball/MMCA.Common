using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Guardrails;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// Redaction is asserted from the innermost client's point of view, because that is the only place
/// it counts: what the provider was actually sent. The caller's own messages are asserted too, from
/// the other side, since a redactor that edited them in place would rewrite a conversation history
/// as a side effect of sending it.
/// </summary>
public sealed class RequestRedactionTests
{
    [Fact]
    public async Task ARedactor_RewritesWhatTheProviderReceives_AndLeavesTheCallersMessagesAlone()
    {
        var caller = new ChatMessage(ChatRole.User, "the secret is 12345");
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(inner, [], [new UppercaseRedactor()]);

        await client.GetResponseAsync([caller], options: null, TestContext.Current.CancellationToken);

        inner.LastMessages.Should().ContainSingle();
        inner.LastMessages[0].Text.Should().Be("THE SECRET IS 12345");
        caller.Text.Should().Be("the secret is 12345", "the caller still holds exactly what it built");
        inner.LastMessages[0].Should().NotBeSameAs(caller);
    }

    [Fact]
    // Redaction runs first: a guardrail is asked about the redacted text, never the original, so it
    // never has to reason about content that was going to be removed anyway.
    public async Task ARedactor_RunsBeforeTheGuardrailsSeeTheMessages()
    {
        string? inspected = null;
        var guardrail = new RecordingGuardrail(messages => inspected = messages[0].Text);
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(inner, [guardrail], [new UppercaseRedactor()]);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            options: null,
            TestContext.Current.CancellationToken);

        inspected.Should().Be("HELLO");
    }

    [Fact]
    public async Task ARedactor_AppliesToTheStreamingPathToo()
    {
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        using var client = new GuardrailChatClient(inner, [], [new UppercaseRedactor()]);

        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            options: null,
            TestContext.Current.CancellationToken))
        {
            _ = update;
        }

        inner.LastMessages[0].Text.Should().Be("HELLO");
    }

    [Fact]
    public async Task EveryRedactorRuns_InRegistrationOrder()
    {
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(
            inner,
            [],
            [new UppercaseRedactor(), new SuffixRedactor("!")]);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            options: null,
            TestContext.Current.CancellationToken);

        inner.LastMessages[0].Text.Should().Be("HELLO!");
    }

    [Fact]
    public void ThePiiGuardrail_RedactsAnEmailAndAPhoneAndLeavesTheRestAlone()
    {
        var redacted = new PiiRedactionGuardrail().Redact(
            [new ChatMessage(ChatRole.User, "Reach Ada at ada@example.com or (404) 555-0134 about the 2026 track.")]);

        redacted[0].Text.Should().Be(
            "Reach Ada at [redacted-email] or [redacted-phone] about the 2026 track.",
            because: "contact details go; the name and the year are evidence and stay");
    }

    [Fact]
    // Copied verbatim from the ADC scorer, narrow on purpose: prose legitimately carries years, team
    // sizes and throughput figures, and redacting those would cost the model its evidence.
    public void ThePiiGuardrail_LeavesOrdinaryNumbersAlone()
    {
        var redacted = new PiiRedactionGuardrail().Redact(
            [new ChatMessage(ChatRole.User, "Led a team of 12 since 2019, shipping 400 releases.")]);

        redacted[0].Text.Should().Be("Led a team of 12 since 2019, shipping 400 releases.");
    }

    [Fact]
    public async Task ThePiiGuardrail_AsARedactor_IsWhatReachesTheProvider()
    {
        var guardrail = new PiiRedactionGuardrail();
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(inner, [guardrail], [guardrail]);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "mail ada@example.com")],
            options: null,
            TestContext.Current.CancellationToken);

        inner.LastMessages[0].Text.Should().Be("mail [redacted-email]");
    }

    [Fact]
    // Instructions are the application's own text, not a user's, so an address in them is there on
    // purpose.
    public async Task ThePiiGuardrail_DoesNotTouchTheInstructions()
    {
        var guardrail = new PiiRedactionGuardrail();
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(inner, [guardrail], [guardrail]);
        var options = new ChatOptions { Instructions = "Escalate to support@mmca.example." };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            options,
            TestContext.Current.CancellationToken);

        inner.LastOptions!.Instructions.Should().Be("Escalate to support@mmca.example.");
    }

    [Fact]
    public async Task ThePiiGuardrail_AsAGuardrail_ShipsNoContentPolicy()
    {
        var guardrail = new PiiRedactionGuardrail();

        var request = await guardrail.InspectRequestAsync([], options: null, TestContext.Current.CancellationToken);
        var response = await guardrail.InspectResponseAsync(new ChatResponse(), options: null, TestContext.Current.CancellationToken);

        request.IsAllowed.Should().BeTrue();
        response.IsAllowed.Should().BeTrue(
            because: "it redacts; deciding what an answer may say stays the application's job (ADR-120)");
    }

    private sealed class UppercaseRedactor : IChatRequestRedactor
    {
        public IReadOnlyList<ChatMessage> Redact(IReadOnlyList<ChatMessage> messages) =>
            [.. messages.Select(message => new ChatMessage(message.Role, message.Text.ToUpperInvariant()))];
    }

    private sealed class SuffixRedactor(string suffix) : IChatRequestRedactor
    {
        public IReadOnlyList<ChatMessage> Redact(IReadOnlyList<ChatMessage> messages) =>
            [.. messages.Select(message => new ChatMessage(message.Role, message.Text + suffix))];
    }

    private sealed class RecordingGuardrail(Action<IReadOnlyList<ChatMessage>> onRequest) : IChatGuardrail
    {
        public ValueTask<GuardrailVerdict> InspectRequestAsync(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
        {
            onRequest(messages);
            return ValueTask.FromResult(GuardrailVerdict.Allow);
        }

        public ValueTask<GuardrailVerdict> InspectResponseAsync(
            ChatResponse response,
            ChatOptions? options,
            CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);
    }
}
