namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Serializes every test class that reads or writes the E2E trigger environment variables. They are
/// process-global, so two classes setting them in parallel would see each other's values.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2eEnvironmentCollection
{
    /// <summary>The collection name the E2E switch tests join with <c>[Collection]</c>.</summary>
    public const string Name = "E2E trigger environment variables";
}

/// <summary>
/// Sets the three E2E trigger variables for the duration of one test and restores all three on
/// dispose. Every variable is set explicitly (null clears it), so a value left on the developer's
/// machine never leaks into an assertion.
/// </summary>
internal sealed class E2eEnvironmentScope : IDisposable
{
    public const string LiftVariable = "E2E_LIFT_REGISTRATION_THROTTLE";
    public const string ForceServerVariable = "E2E_FORCE_SERVER";
    public const string ForceWasmVariable = "E2E_FORCE_WASM";

    private static readonly string[] Variables = [LiftVariable, ForceServerVariable, ForceWasmVariable];

    private readonly Dictionary<string, string?> _original;

    public E2eEnvironmentScope(string? lift = null, string? forceServer = null, string? forceWasm = null)
    {
        _original = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        Environment.SetEnvironmentVariable(LiftVariable, lift);
        Environment.SetEnvironmentVariable(ForceServerVariable, forceServer);
        Environment.SetEnvironmentVariable(ForceWasmVariable, forceWasm);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
