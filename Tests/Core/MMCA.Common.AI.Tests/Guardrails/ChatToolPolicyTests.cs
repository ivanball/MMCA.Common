using System.Text.Json;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Guardrails;

namespace MMCA.Common.AI.Tests.Guardrails;

/// <summary>
/// L245: the confirmed-tools stamp is read in every shape a caller can hand it over in, including the
/// <see cref="JsonElement"/> values a JSON-bound <see cref="ChatOptions.AdditionalProperties"/> bag holds.
/// </summary>
public sealed class ChatToolPolicyTests
{
    private static readonly string[] TwoNames = ["send_email", "delete_row"];

    [Fact]
    public void ReadConfirmedTools_ReadsAJsonArrayOfNames()
    {
        var options = Stamped(JsonSerializer.SerializeToElement(TwoNames));

        ChatToolPolicy.ReadConfirmedTools(options).Should().BeEquivalentTo(TwoNames);
    }

    [Fact]
    public void ReadConfirmedTools_ReadsAJsonString()
    {
        var options = Stamped(JsonSerializer.SerializeToElement("send_email"));

        ChatToolPolicy.ReadConfirmedTools(options).Should().ContainSingle().Which.Should().Be("send_email");
    }

    [Fact]
    public void ReadConfirmedTools_IgnoresAJsonNumber()
    {
        var options = Stamped(JsonSerializer.SerializeToElement(42));

        ChatToolPolicy.ReadConfirmedTools(options).Should().BeEmpty();
    }

    private static ChatOptions Stamped(JsonElement value) => new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatToolPolicy.ConfirmedToolsPropertyKey] = value,
        },
    };
}
