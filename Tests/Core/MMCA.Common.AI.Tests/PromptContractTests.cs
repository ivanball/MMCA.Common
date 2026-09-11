using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// The hash is the load-bearing part: an evaluation gate keys its recorded answers on it, so it has
/// to be stable across checkouts (CRLF versus LF) and sensitive to every component that can change
/// what the model answers.
/// </summary>
public sealed class PromptContractTests
{
    private static PromptContract Sample(string systemPrompt = "You are a scorer.\nBe terse.") =>
        new("session-scoring", "3", "claude-haiku-4-5", systemPrompt);

    [Fact]
    public void Hash_IsLowercaseHexSha256() => Sample().Hash.Should().MatchRegex("^[0-9a-f]{64}$");

    [Fact]
    public void Hash_IgnoresLineEndingStyle()
    {
        var lf = Sample("Line one.\nLine two.\n");
        var crlf = Sample("Line one.\r\nLine two.\r\n");
        var cr = Sample("Line one.\rLine two.\r");

        crlf.Hash.Should().Be(lf.Hash);
        cr.Hash.Should().Be(lf.Hash);
    }

    [Fact]
    public void Hash_IsStableAcrossEqualInstances() => Sample().Hash.Should().Be(Sample().Hash);

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("model")]
    [InlineData("systemPrompt")]
    public void Hash_ChangesWhenAnyComponentChanges(string component)
    {
        var original = Sample();

        var changed = component switch
        {
            "name" => original with { Name = "session-scoring-v2" },
            "version" => original with { Version = "4" },
            "model" => original with { Model = "claude-sonnet-4-5" },
            _ => original with { SystemPrompt = original.SystemPrompt + " Cite sources." },
        };

        changed.Hash.Should().NotBe(original.Hash);
    }

    [Fact]
    public void Hash_DoesNotSurviveAWithExpression()
    {
        // A record's copy constructor copies fields verbatim, so a cached hash would describe the
        // prompt the copy was made FROM. This is the regression test for that trap.
        var original = Sample();
        _ = original.Hash;

        var changed = original with { Version = "99" };

        changed.Hash.Should().NotBe(original.Hash);
    }

    [Fact]
    public void ComponentsAreSeparated_SoAShiftDoesNotCollide()
    {
        var first = new PromptContract("ab", "c", "m", "p");
        var second = new PromptContract("a", "bc", "m", "p");

        first.Hash.Should().NotBe(second.Hash);
    }

    [Fact]
    public void ToChatOptions_CarriesTheModelPromptAndIdentity()
    {
        var contract = Sample();

        var options = contract.ToChatOptions();

        options.ModelId.Should().Be("claude-haiku-4-5");
        options.Instructions.Should().Be(contract.SystemPrompt);
        options.AdditionalProperties.Should().NotBeNull();
        options.AdditionalProperties![PromptContract.NamePropertyKey].Should().Be("session-scoring");
        options.AdditionalProperties[PromptContract.VersionPropertyKey].Should().Be("3");
        options.AdditionalProperties[PromptContract.HashPropertyKey].Should().Be(contract.Hash);
    }

    [Fact]
    public void Apply_StampsOntoOptionsTheCallerAlreadyBuilt()
    {
        var contract = Sample();
        var options = new ChatOptions { Temperature = 0.1f };

        var stamped = contract.Apply(options);

        stamped.Should().BeSameAs(options);
        options.Temperature.Should().Be(0.1f);
        PromptContract.ReadName(options).Should().Be("session-scoring");
        PromptContract.ReadVersion(options).Should().Be("3");
    }

    [Fact]
    public void ReadName_AndReadVersion_AnswerNullForUnstampedOptions()
    {
        PromptContract.ReadName(null).Should().BeNull();
        PromptContract.ReadName(new ChatOptions()).Should().BeNull();
        PromptContract.ReadVersion(new ChatOptions()).Should().BeNull();
    }
}
