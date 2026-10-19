using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for the CI/E2E forced-InteractiveServer switch. Like the sibling lifts, they assert the
/// application MODEL: a triggered call writes <c>E2E__ForceServer=true</c> onto the UI resource, and an
/// untriggered or vetoed call writes nothing.
/// </summary>
public sealed class E2eForceServerTests
{
    private const string TriggerVariable = "E2E_FORCE_SERVER";
    private const string ForceServerKey = "E2E__ForceServer";

    [Fact]
    public async Task WithE2eForceServer_WhenTheEnvironmentAsks_SetsForceServer()
    {
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: "true");

        environment.Should().ContainKey(ForceServerKey)
            .WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task WithE2eForceServer_TriggerValueIsCaseInsensitive()
    {
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: "TRUE");

        environment.Should().ContainKey(ForceServerKey);
    }

    [Fact]
    public async Task WithE2eForceServer_WhenTheEnvironmentIsUnset_WritesNothing()
    {
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: null);

        environment.Should().NotContainKey(ForceServerKey);
    }

    [Fact]
    public async Task WithE2eForceServer_WhenTheEnvironmentSaysFalse_WritesNothing()
    {
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: "false");

        environment.Should().NotContainKey(ForceServerKey);
    }

    [Fact]
    public async Task WithE2eForceServer_WhenUnlessAlsoIsSet_WritesNothingEvenIfTheEnvironmentAsks()
    {
        // The veto is how an AppHost lets a competing render-mode switch (forced WebAssembly) win.
        Dictionary<string, string> environment = await ForceServerAsync(triggerValue: "true", unlessAlso: true);

        environment.Should().NotContainKey(ForceServerKey);
    }

    [Fact]
    public void WithE2eForceServer_ReturnsTheSameBuilder_SoItChains()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var ui = builder.AddResource(new ProjectResource("ui"));

        ui.WithE2eForceServer().Should().BeSameAs(ui);
        ui.WithE2eForceServer(unlessAlso: true).Should().BeSameAs(ui);
    }

    [Fact]
    public void PublicConstants_MatchTheWireNames()
    {
        Extensions.E2eForceServerTriggerVariable.Should().Be(TriggerVariable);
        Extensions.E2eForceServerConfigurationKey.Should().Be(ForceServerKey);
    }

    private static async Task<Dictionary<string, string>> ForceServerAsync(string? triggerValue, bool unlessAlso = false)
    {
        var original = Environment.GetEnvironmentVariable(TriggerVariable);
        try
        {
            Environment.SetEnvironmentVariable(TriggerVariable, triggerValue);

            var builder = DistributedApplication.CreateBuilder([]);

            // AddResource rather than AddProject: AddProject validates that the project file exists.
            var ui = builder.AddResource(new ProjectResource("ui"));

            ui.WithE2eForceServer(unlessAlso);

            return await EnvironmentOf(builder, ui.Resource);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TriggerVariable, original);
        }
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
