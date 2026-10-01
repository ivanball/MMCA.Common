using Microsoft.Extensions.AI;
using MMCA.Common.AI.Guardrails;

namespace MMCA.Common.AI.Tests.Fixtures;

/// <summary>
/// A tool policy whose answer the test supplies, so the composition rule (every policy must allow)
/// is exercised without the package shipping a tool allow-list of its own.
/// </summary>
internal sealed class StubToolPolicy(ToolAuthorization authorization = ToolAuthorization.Allowed) : IChatToolPolicy
{
    /// <summary>How many tools this policy was asked about.</summary>
    public int Authorizations { get; private set; }

    public ToolAuthorization Authorize(AITool tool, ChatOptions options)
    {
        Authorizations++;
        return authorization;
    }
}

/// <summary>
/// A tool that is nothing but a name and a property bag: enough for the policy layer, which never
/// invokes anything, and free of any guess about how a given factory exposes those two.
/// </summary>
internal sealed class StubTool(string name, bool consequential = false) : AITool
{
    private IReadOnlyDictionary<string, object?>? _markedProperties;

    public override string Name => name;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties =>
        _markedProperties ?? (consequential
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ChatToolPolicy.ConsequentialPropertyKey] = true,
            }
            : new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>
    /// A tool whose consequential marker carries <paramref name="marker"/> as-is, so the policy layer
    /// can be driven with the shapes a settings file or a JSON-bound bag hands over.
    /// </summary>
    public static StubTool WithMarker(string name, object? marker) => new(name)
    {
        _markedProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ChatToolPolicy.ConsequentialPropertyKey] = marker,
        },
    };
}
