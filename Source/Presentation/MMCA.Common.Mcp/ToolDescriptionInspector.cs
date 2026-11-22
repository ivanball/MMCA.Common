using System.Text.Json;
using ModelContextProtocol.Server;

namespace MMCA.Common.Mcp;

/// <summary>
/// Finds text a model reads but a reviewer does not see (R023, the check lives in
/// <see cref="McpHiddenText"/>). Run over every tool, parameter and server description before the endpoint
/// is mapped, so a poisoned description fails startup instead of reaching a client.
/// </summary>
internal static class ToolDescriptionInspector
{
    /// <summary>Returns one finding per hidden-text occurrence in <paramref name="text"/>; empty when clean.</summary>
    /// <param name="text">The description to inspect.</param>
    /// <returns>The findings, each naming the code point or the comment and its position.</returns>
    public static IReadOnlyList<string> FindHiddenText(string? text) => McpHiddenText.Find(text);

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
