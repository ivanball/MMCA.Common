using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace MMCA.Common.Mcp;

/// <summary>
/// Finds text a model reads but a reviewer does not see (R023): a Unicode format character
/// (category Cf: zero-width characters, bidi controls), a tag character (U+E0000 to U+E007F) or an
/// HTML comment opener. Run over every tool, parameter and server description before the endpoint
/// is mapped, so a poisoned description fails startup instead of reaching a client.
/// </summary>
internal static class ToolDescriptionInspector
{
    private const string HtmlCommentOpener = "<!--";

    /// <summary>Returns one finding per hidden-text occurrence in <paramref name="text"/>; empty when clean.</summary>
    /// <param name="text">The description to inspect.</param>
    /// <returns>The findings, each naming the code point or the comment and its position.</returns>
    public static IReadOnlyList<string> FindHiddenText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        List<string> findings = [];
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsTagCharacter(rune))
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"tag character U+{rune.Value:X4} at {index}"));
            }
            else if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"format character U+{rune.Value:X4} at {index}"));
            }

            index += rune.Utf16SequenceLength;
        }

        var comment = text.IndexOf(HtmlCommentOpener, StringComparison.Ordinal);
        while (comment >= 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"HTML comment at {comment}"));
            comment = text.IndexOf(HtmlCommentOpener, comment + HtmlCommentOpener.Length, StringComparison.Ordinal);
        }

        return findings;
    }

    /// <summary>
    /// Inspects every tool (name, title, description and each string in its input and output
    /// schema, which is where parameter descriptions live) and the server's instructions and
    /// identity, and throws when any of them hides text.
    /// </summary>
    /// <param name="options">The configured server options, tool collection included.</param>
    /// <exception cref="InvalidOperationException">At least one description hides text.</exception>
    public static void ThrowIfHiddenText(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> problems = [];

        Collect(problems, "server instructions", options.ServerInstructions);
        if (options.ServerInfo is { } serverInfo)
        {
            Collect(problems, "server title", serverInfo.Title);
            Collect(problems, "server description", serverInfo.Description);
        }

        foreach (var protocolTool in (options.ToolCollection ?? []).Select(static tool => tool.ProtocolTool))
        {
            var owner = $"tool '{protocolTool.Name}'";
            Collect(problems, $"{owner} name", protocolTool.Name);
            Collect(problems, $"{owner} title", protocolTool.Title);
            Collect(problems, $"{owner} annotation title", protocolTool.Annotations?.Title);
            Collect(problems, $"{owner} description", protocolTool.Description);
            CollectSchema(problems, $"{owner} input schema", protocolTool.InputSchema);
            if (protocolTool.OutputSchema is { } outputSchema)
            {
                CollectSchema(problems, $"{owner} output schema", outputSchema);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "MCP endpoint refused to start: a description hides text a model would read and a reviewer would not see "
                + "(a Unicode format character, a tag character or an HTML comment). Remove it from: "
                + string.Join("; ", problems) + ".");
        }
    }

    private static bool IsTagCharacter(Rune rune) => rune.Value is >= 0xE0000 and <= 0xE007F;

    private static void Collect(List<string> problems, string where, string? text)
    {
        var findings = FindHiddenText(text);
        if (findings.Count > 0)
        {
            problems.Add($"{where} ({string.Join(", ", findings)})");
        }
    }

    // A parameter's [Description] lands in the generated JSON schema, so every property name and
    // string value in it is model-visible text and is inspected, not just "description" keys.
    private static void CollectSchema(List<string> problems, string where, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            Collect(problems, where, element.GetString());
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Collect(problems, where, property.Name);
                CollectSchema(problems, where, property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectSchema(problems, where, item);
            }
        }
    }
}
