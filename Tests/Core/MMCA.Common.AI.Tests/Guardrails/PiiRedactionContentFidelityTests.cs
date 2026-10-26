using Microsoft.Extensions.AI;
using MMCA.Common.AI.Guardrails;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// M239: the PII redactor rewrites the text a content item carries and nothing else. A signed
/// reasoning block (its <see cref="TextReasoningContent.ProtectedData"/> is the provider's signature
/// over the text, Anthropic's thinking signature for one) passes through by reference, because an
/// edited signed block is rejected on the next turn. Every other rebuilt item keeps its metadata.
/// The one property a REDACTED item does not keep is <see cref="AIContent.RawRepresentation"/>: the
/// provider's original object still holds the unredacted text, and the OpenAI adapters send it
/// verbatim in place of the redacted item, so copying it would undo the redaction. An item the
/// redactor did not change passes through by reference with its raw representation intact.
/// </summary>
public sealed class PiiRedactionContentFidelityTests
{
    private static readonly AdditionalPropertiesDictionary Extra = new() { ["provider.hint"] = "kept" };

    [Fact]
    public void ASignedReasoningBlock_PassesThroughByReference()
    {
        var signed = new TextReasoningContent("the user is ada@example.com") { ProtectedData = "signature" };

        var redacted = Redact(ChatRole.Assistant, signed);

        redacted.Should().BeSameAs(signed, "editing a signed thinking block invalidates its signature");
    }

    [Fact]
    public void AnUnsignedReasoningBlock_IsRedacted_AndKeepsItsMetadata()
    {
        var citation = new CitationAnnotation { Title = "source" };
        var reasoning = new TextReasoningContent("the user is ada@example.com")
        {
            AdditionalProperties = Extra,
            Annotations = [citation],
            RawRepresentation = new object(),
        };

        var redacted = Redact(ChatRole.Assistant, reasoning).Should().BeOfType<TextReasoningContent>().Subject;

        redacted.Text.Should().Be("the user is [redacted-email]");
        redacted.AdditionalProperties.Should().BeSameAs(Extra);
        redacted.Annotations.Should().ContainSingle().Which.Should().BeSameAs(citation);
        redacted.RawRepresentation.Should().BeNull("the raw object still carries the unredacted text");
    }

    [Fact]
    public void ARedactedToolCall_KeepsInformationalOnlyExceptionAndMetadata()
    {
        var failure = new InvalidOperationException("bad arguments");
        var call = new FunctionCallContent("call-1", "email_user", new Dictionary<string, object?> { ["to"] = "jane@example.com" })
        {
            InformationalOnly = true,
            Exception = failure,
            AdditionalProperties = Extra,
            RawRepresentation = new object(),
        };

        var redacted = Redact(ChatRole.Assistant, call).Should().BeOfType<FunctionCallContent>().Subject;

        redacted.Arguments!["to"].Should().Be("[redacted-email]");
        redacted.CallId.Should().Be("call-1");
        redacted.InformationalOnly.Should().BeTrue();
        redacted.Exception.Should().BeSameAs(failure);
        redacted.AdditionalProperties.Should().BeSameAs(Extra);
        redacted.RawRepresentation.Should().BeNull("the raw tool call still carries the unredacted arguments");
    }

    [Fact]
    public void ARedactedToolResult_KeepsItsExceptionAndMetadata()
    {
        var failure = new InvalidOperationException("tool failed");
        var result = new FunctionResultContent("call-2", "reach me at jane@example.com")
        {
            Exception = failure,
            AdditionalProperties = Extra,
        };

        var redacted = Redact(ChatRole.Tool, result).Should().BeOfType<FunctionResultContent>().Subject;

        redacted.Result.Should().Be("reach me at [redacted-email]");
        redacted.Exception.Should().BeSameAs(failure);
        redacted.AdditionalProperties.Should().BeSameAs(Extra);
    }

    [Fact]
    public void ARedactedText_KeepsItsMetadata_ButNotItsRawRepresentation()
    {
        var text = new TextContent("mail ada@example.com") { AdditionalProperties = Extra, RawRepresentation = new object() };

        var redacted = Redact(ChatRole.User, text).Should().BeOfType<TextContent>().Subject;

        redacted.Text.Should().Be("mail [redacted-email]");
        redacted.AdditionalProperties.Should().BeSameAs(Extra);
        redacted.RawRepresentation.Should().BeNull();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("reasoning")]
    [InlineData("call")]
    [InlineData("result")]
    public void AnItemWithNothingToRedact_PassesThroughByReference(string kind)
    {
        AIContent clean = kind switch
        {
            "text" => new TextContent("no contact details here") { RawRepresentation = new object() },
            "reasoning" => new TextReasoningContent("thinking about 2019") { RawRepresentation = new object() },
            "call" => new FunctionCallContent("call-3", "lookup", new Dictionary<string, object?> { ["id"] = "42" }) { RawRepresentation = new object() },
            _ => new FunctionResultContent("call-3", System.Text.Json.JsonSerializer.SerializeToElement(new { name = "Ada", since = 1700000000 })) { RawRepresentation = new object() },
        };

        Redact(ChatRole.Assistant, clean).Should().BeSameAs(clean, "nothing was redacted, so nothing about it needs to change");
    }

    // L244: the rebuilt message keeps its CreatedAt stamp.
    [Fact]
    public void ARedactedMessage_KeepsItsCreatedAt()
    {
        var createdAt = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        var message = new ChatMessage(ChatRole.User, "write to ada@example.com") { CreatedAt = createdAt };

        var redacted = new PiiRedactionGuardrail().Redact([message])[0];

        redacted.Text.Should().Be("write to [redacted-email]");
        redacted.CreatedAt.Should().Be(createdAt);
    }

    private static AIContent Redact(ChatRole role, AIContent content) =>
        new PiiRedactionGuardrail().Redact([new ChatMessage(role, [content])])[0].Contents.Should().ContainSingle().Subject;
}
