using AwesomeAssertions;

namespace MMCA.Common.Mcp.Tests;

/// <summary>
/// R023 applied to tool results: the public check finds the same hidden text the startup inspection
/// refuses, and <see cref="McpHiddenText.Strip"/> removes every invisible character from third-party
/// text while keeping the visible text verbatim.
/// </summary>
public sealed class McpHiddenTextTests
{
    public static TheoryData<string, string> InvisibleCharacters => new()
    {
        { "zero-width space", "\u200B" },
        { "right-to-left override", "\u202E" },
        { "zero-width joiner", "\u200D" },
        { "soft hyphen", "\u00AD" },
        { "byte order mark", "\uFEFF" },
        { "tag character A", "\U000E0041" },
        { "cancel tag", "\U000E007F" },
    };

    [Theory]
    [MemberData(nameof(InvisibleCharacters))]
    public void Strip_RemovesTheInvisibleCharacter_AndKeepsTheVisibleText(string name, string invisible)
    {
        var cleaned = McpHiddenText.Strip($"Ada{invisible} Lovelace{invisible}");

        cleaned.Should().Be("Ada Lovelace", because: $"a {name} is never forwarded");
        McpHiddenText.Find(cleaned).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvisibleCharacters))]
    public void Find_ReportsTheInvisibleCharacter(string name, string invisible) =>
        McpHiddenText.Find($"Bio{invisible}").Should().ContainSingle(because: $"a {name} is hidden text");

    [Fact]
    public void Strip_RemovesAWholeTagCharacterRun_SmuggledAfterVisibleText()
    {
        // "Hi" followed by the tag-character spelling of "ignore": invisible in most renderers, read
        // by a model as text.
        var smuggled = "Hi" + string.Concat("ignore".Select(static c => char.ConvertFromUtf32(0xE0000 + c)));

        McpHiddenText.Strip(smuggled).Should().Be("Hi");
    }

    [Theory]
    [InlineData("Plain ASCII text.")]
    [InlineData("Ren\u00E9e \u00D6zt\u00FCrk, \u6771\u4EAC")]
    [InlineData("Emoji \U0001F680 stays")]
    [InlineData("<!-- a visible comment opener stays -->")]
    public void Strip_LeavesTextWithNoInvisibleCharacterUnchanged(string text) =>
        McpHiddenText.Strip(text).Should().BeSameAs(text);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_ReturnsNullOrEmptyInputUnchanged(string? text)
    {
        McpHiddenText.Strip(text).Should().Be(text);
        McpHiddenText.Find(text).Should().BeEmpty();
    }

    [Fact]
    public void Find_ReportsAnHtmlCommentOpener() =>
        McpHiddenText.Find("Talk <!-- secret -->").Should().ContainSingle().Which.Should().Contain("HTML comment");
}
