using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for the local forced-WebAssembly switch (item 25, rules 1 and 4). Like the sibling
/// switches they assert the application MODEL: a triggered call writes
/// <c>E2E__ForceWebAssembly=true</c> onto the UI resource, and an untriggered call writes nothing.
/// </summary>
[Collection(E2eEnvironmentCollection.Name)]
public sealed class E2eForceWebAssemblyTests
{
    private const string TriggerVariable = "E2E_FORCE_WASM";
    private const string ForceWebAssemblyKey = "E2E__ForceWebAssembly";

    [Fact]
    public async Task WhenTheEnvironmentAsks_SetsForceWebAssembly()
    {
        Dictionary<string, string> environment = await ForceWebAssemblyAsync(forceWasmValue: "true");

        environment.Should().ContainKey(ForceWebAssemblyKey)
            .WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task TriggerIsCaseInsensitive()
    {
        Dictionary<string, string> environment = await ForceWebAssemblyAsync(forceWasmValue: "TRUE");

        environment.Should().ContainKey(ForceWebAssemblyKey);
    }

    [Fact]
    public async Task Unset_WritesNothing()
    {
        Dictionary<string, string> environment = await ForceWebAssemblyAsync(forceWasmValue: null);

        environment.Should().NotContainKey(ForceWebAssemblyKey);
    }

    [Fact]
    public async Task False_WritesNothing()
    {
        Dictionary<string, string> environment = await ForceWebAssemblyAsync(forceWasmValue: "false");

        environment.Should().NotContainKey(ForceWebAssemblyKey);
    }

    [Fact]
    public void ReturnsTheSameBuilder()
    {
        using var scope = new E2eEnvironmentScope(forceWasm: "true");
        var builder = DistributedApplication.CreateBuilder([]);
        var ui = builder.AddResource(new ProjectResource("ui"));

        ui.WithE2eForceWebAssembly().Should().BeSameAs(ui);
    }

    [Fact]
    public void PublicConstants_MatchTheWireNames()
    {
        Extensions.E2eForceWebAssemblyTriggerVariable.Should().Be(TriggerVariable);
        Extensions.E2eForceWebAssemblyConfigurationKey.Should().Be(ForceWebAssemblyKey);
    }

    private static async Task<Dictionary<string, string>> ForceWebAssemblyAsync(string? forceWasmValue)
    {
        using var scope = new E2eEnvironmentScope(forceWasm: forceWasmValue);

        var builder = DistributedApplication.CreateBuilder([]);

        // AddResource rather than AddProject: AddProject validates that the project file exists.
        var ui = builder.AddResource(new ProjectResource("ui"));

        ui.WithE2eForceWebAssembly();

        return await EnvironmentOf(builder, ui.Resource);
    }

    private static async Task<Dictionary<string, string>> EnvironmentOf(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = services,
            });

        IExecutionConfigurationResult result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        result.Exception.Should().BeNull();
        return result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }
}
