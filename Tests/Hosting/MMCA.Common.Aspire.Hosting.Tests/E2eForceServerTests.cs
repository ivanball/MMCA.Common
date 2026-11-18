using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using static MMCA.Common.Aspire.Hosting.Tests.Support.PublishModeEnvironment;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for the CI/E2E forced-InteractiveServer switch. Like the sibling lifts, they assert the
/// application MODEL: a triggered call writes <c>E2E__ForceServer=true</c> onto the UI resource, and an
/// untriggered or vetoed call writes nothing. The veto is now the <c>E2E_FORCE_WASM</c> environment
/// variable itself (item 25, rule 3), not a call-site argument.
/// </summary>
[Collection(E2eEnvironmentCollection.Name)]
public sealed class E2eForceServerTests
{
    private const string TriggerVariable = "E2E_FORCE_SERVER";
    private const string ForceServerKey = "E2E__ForceServer";

    // The trigger value is case-insensitive.
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task WithE2eForceServer_WhenTheEnvironmentAsks_SetsForceServer(string triggerValue)
    {
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue);

        environment.Should().ContainKey(ForceServerKey)
            .WhoseValue.Should().Be("true");
    }

    // Unset (null) or "false".
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task WithE2eForceServer_WhenTheEnvironmentDoesNotAsk_WritesNothing(string? triggerValue)
    {
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue);

        environment.Should().NotContainKey(ForceServerKey);
    }

    [Fact]
    public async Task WhenForceWasmIsAlsoSet_WritesNothing()
    {
        // The veto is how forced WebAssembly wins over forced Server; it is read from the environment.
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: "true", forceWasmValue: "true");

        environment.Should().NotContainKey(ForceServerKey);
    }

    [Fact]
    public async Task WhenForceWasmSaysFalse_StillSetsForceServer()
    {
        // Only a "true" vetoes: the WASM variable present but false must not suppress the switch.
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: "true", forceWasmValue: "false");

        environment.Should().ContainKey(ForceServerKey);
    }

    [Fact]
    public void WithE2eForceServer_ReturnsTheSameBuilder_SoItChains()
    {
        using var scope = new E2eEnvironmentScope(forceServer: "true");
        var builder = DistributedApplication.CreateBuilder([]);
        var ui = builder.AddResource(new ProjectResource("ui"));

        ui.WithE2eForceServer().Should().BeSameAs(ui);
    }

    [Fact]
    public void PublicConstants_MatchTheWireNames()
    {
        Extensions.E2eForceServerTriggerVariable.Should().Be(TriggerVariable);
        Extensions.E2eForceServerConfigurationKey.Should().Be(ForceServerKey);
    }

    private static async Task<Dictionary<string, string>> ForceServerAsync(string? triggerValue, string? forceWasmValue = null)
    {
        using var scope = new E2eEnvironmentScope(forceServer: triggerValue, forceWasm: forceWasmValue);

        var builder = DistributedApplication.CreateBuilder([]);

        // AddResource rather than AddProject: AddProject validates that the project file exists.
        var ui = builder.AddResource(new ProjectResource("ui"));

        ui.WithE2eForceServer();

        return await EnvironmentOf(builder, ui.Resource);
    }
}
