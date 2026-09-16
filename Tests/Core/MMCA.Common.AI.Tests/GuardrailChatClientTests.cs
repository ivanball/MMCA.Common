using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// The guardrail layer is an extension point, not a policy (ADR-120): these facts pin what the
/// framework guarantees about WHEN an application's guardrail runs and what a block does, never what
/// a guardrail should consider unsafe.
/// </summary>
public sealed class GuardrailChatClientTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new(ChatRole.User, "score this")];

    [Fact]
    public async Task AnAllowingGuardrail_LetsTheCallThrough()
    {
        var guardrail = new StubGuardrail();
        using var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var response = await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        response.Text.Should().Be("ok");
        inner.CallCount.Should().Be(1);
        guardrail.RequestInspections.Should().Be(1);
        guardrail.ResponseInspections.Should().Be(1);
    }

    [Fact]
    public async Task ABlockedRequest_ThrowsBeforeTheProviderIsReached()
    {
        var guardrail = new StubGuardrail(requestVerdict: GuardrailVerdict.Block("contains a customer record"));
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var act = async () => await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ChatGuardrailException>())
            .WithMessage("contains a customer record");
        inner.CallCount.Should().Be(0, "a refused request must cost nothing");
        guardrail.ResponseInspections.Should().Be(0);
    }

    [Fact]
    public async Task ABlockedResponse_ThrowsInsteadOfReturningIt()
    {
        var guardrail = new StubGuardrail(responseVerdict: GuardrailVerdict.Block("leaked an internal hostname"));
        using var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "srv-01.internal")));
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var act = async () => await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ChatGuardrailException>())
            .WithMessage("leaked an internal hostname");
    }

    [Fact]
    // Every registered guardrail runs, and the first block stops the call: a second concern cannot be
    // skipped because the first one happened to allow.
    public async Task EveryGuardrailRuns_AndTheFirstBlockWins()
    {
        var permissive = new StubGuardrail();
        var strict = new StubGuardrail(requestVerdict: GuardrailVerdict.Block("no"));
        var never = new StubGuardrail();
        using var inner = new StubChatClient();
        using var client = new GuardrailChatClient(inner, [permissive, strict, never]);

        var act = async () => await client.GetResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChatGuardrailException>();
        permissive.RequestInspections.Should().Be(1);
        strict.RequestInspections.Should().Be(1);
        never.RequestInspections.Should().Be(0, "the chain stops at the first block");
    }

    [Fact]
    // Documented asymmetry: buffering a streamed answer to inspect it defeats the reason a caller
    // chose streaming, so the streaming path inspects the request only.
    public async Task TheStreamingPath_InspectsTheRequestOnly()
    {
        var guardrail = new StubGuardrail();
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        using var client = new GuardrailChatClient(inner, [guardrail]);

        await foreach (var update in client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken))
        {
            _ = update;
        }

        guardrail.RequestInspections.Should().Be(1);
        guardrail.ResponseInspections.Should().Be(0);
    }

    [Fact]
    public async Task ABlockedRequest_StopsTheStreamBeforeTheProviderIsReached()
    {
        var guardrail = new StubGuardrail(requestVerdict: GuardrailVerdict.Block("no"));
        using var inner = new StubChatClient(updates: [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        using var client = new GuardrailChatClient(inner, [guardrail]);

        var act = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(Prompt, options: null, TestContext.Current.CancellationToken))
            {
                _ = update;
            }
        };

        await act.Should().ThrowAsync<ChatGuardrailException>();
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    // The default verdict is a block, so a half-built value fails closed. It carries no reason of its
    // own, which is what GuardrailVerdict.UnspecifiedReason stands in for at the throw site.
    public void TheDefaultVerdict_IsABlockWithTheUnspecifiedReason()
    {
        default(GuardrailVerdict).IsAllowed.Should().BeFalse();
        default(GuardrailVerdict).Reason.Should().BeNull();
        GuardrailVerdict.Allow.IsAllowed.Should().BeTrue();
        GuardrailVerdict.Block("because").Reason.Should().Be("because");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Block_RejectsAReasonThatSaysNothing(string reason) =>
        Assert.Throws<ArgumentException>(() => GuardrailVerdict.Block(reason));
}
