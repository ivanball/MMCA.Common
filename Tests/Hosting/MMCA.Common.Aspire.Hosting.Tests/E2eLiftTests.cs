using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Gateway;
using MMCA.Common.Gateway;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for the two CI/E2E lifts: the gateway edge rate-limit lift and the Identity
/// registration-throttle lift it shares its trigger with. They assert the application MODEL (which
/// environment variables a triggered lift writes, and that an untriggered one writes nothing), which
/// is the whole contract: the behavioural proof is E2E-only, since a silent regression here reappears
/// as login/register reds rather than as a failing build.
/// <para>
/// The emitted key names are cross-asserted against the Common-owned settings types they belong to.
/// <c>MMCA.Common.Aspire.Hosting</c> deliberately does not reference <c>MMCA.Common.Aspire</c> or
/// <c>MMCA.Common.Gateway</c> (an AppHost-tier package must not drag in the service-defaults graph),
/// so the section names are mirrored as constants there; this test project references all three and
/// is what keeps the mirror honest, so a section or property rename cannot silently orphan the lift.
/// </para>
/// </summary>
public sealed class E2eLiftTests
{
    private const string TriggerVariable = "E2E_LIFT_REGISTRATION_THROTTLE";

    [Fact]
    public async Task WithE2eGatewayRateLimitLift_WhenTheEnvironmentAsks_SetsAllThreeOverrides()
    {
        Dictionary<string, string> environment = await LiftAsync(triggerValue: "true");

        environment.Should().ContainKey("GatewayRateLimiting__PermitLimit")
            .WhoseValue.Should().Be("100000");
        environment.Should().ContainKey("GatewayRateLimiting__GlobalConcurrencyLimit")
            .WhoseValue.Should().Be("10000");
        environment.Should().ContainKey("MmcaGateway__RateLimiterPolicies__auth-tight__PermitLimit")
            .WhoseValue.Should().Be("100000");
    }

    [Fact]
    public async Task WithE2eGatewayRateLimitLift_WhenNeitherTriggerFires_WritesNothing()
    {
        // Inert locally and in production is the point: the gateway keeps the real limits from its own
        // appsettings, and this method is safe to call unconditionally in every AppHost.
        Dictionary<string, string> environment = await LiftAsync(triggerValue: null);

        environment.Keys.Should().NotContain(key => key.StartsWith("GatewayRateLimiting__", StringComparison.Ordinal));
        environment.Keys.Should().NotContain(key => key.StartsWith("MmcaGateway__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithE2eGatewayRateLimitLift_WhenTheEnvironmentSaysFalse_WritesNothing()
    {
        Dictionary<string, string> environment = await LiftAsync(triggerValue: "false");

        environment.Should().NotContainKey("GatewayRateLimiting__PermitLimit");
    }

    [Fact]
    public async Task WithE2eGatewayRateLimitLift_WhenOnlyTheCallSiteFlagFires_SetsAllThreeOverrides()
    {
        // The OR disjunct is what absorbs an AppHost's own E2E switch without a second environment
        // variable: one consumer passes its forced-render-mode flag here.
        Dictionary<string, string> environment = await LiftAsync(triggerValue: null, alsoLiftWhen: true);

        environment.Should().ContainKey("GatewayRateLimiting__PermitLimit");
        environment.Should().ContainKey("GatewayRateLimiting__GlobalConcurrencyLimit");
        environment.Should().ContainKey("MmcaGateway__RateLimiterPolicies__auth-tight__PermitLimit");
    }

    [Fact]
    public async Task WithE2eGatewayRateLimitLift_EmitsKeysDerivedFromTheCommonOwnedSectionNames()
    {
        Dictionary<string, string> environment = await LiftAsync(triggerValue: "true");

        environment.Should().ContainKey(
            $"{GatewayRateLimitingSettings.SectionName}__{nameof(GatewayRateLimitingSettings.PermitLimit)}");
        environment.Should().ContainKey(
            $"{GatewayRateLimitingSettings.SectionName}__{nameof(GatewayRateLimitingSettings.GlobalConcurrencyLimit)}");
        environment.Should().ContainKey(
            $"{GatewaySettings.SectionName}__{nameof(GatewaySettings.RateLimiterPolicies)}__auth-tight__"
            + nameof(GatewayRoutePolicySettings.PermitLimit));
    }

    [Fact]
    public void WithE2eGatewayRateLimitLift_ReturnsTheSameBuilder_SoItChains()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var gateway = AddGateway(builder);

        gateway.WithE2eGatewayRateLimitLift(alsoLiftWhen: true).Should().BeSameAs(gateway);
        gateway.WithE2eGatewayRateLimitLift().Should().BeSameAs(gateway);
    }

    [Fact]
    public async Task WithE2eRegistrationThrottleLift_SharesTheGatewayLiftsTrigger()
    {
        // Both lifts exist because of the same single-loopback-IP E2E suite, so they must not be able
        // to drift onto different switches: one workflow variable turns both on.
        var original = Environment.GetEnvironmentVariable(TriggerVariable);
        try
        {
            Environment.SetEnvironmentVariable(TriggerVariable, "true");

            var builder = DistributedApplication.CreateBuilder([]);
            var identity = builder.AddResource(new ProjectResource("identity"));
            identity.WithE2eRegistrationThrottleLift();

            Dictionary<string, string> environment = await EnvironmentOf(builder, identity.Resource);

            environment.Should().ContainKey("LoginProtection__MaxRegistrationsPerIpPerHour")
                .WhoseValue.Should().Be("1000");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TriggerVariable, original);
        }
    }

    private static async Task<Dictionary<string, string>> LiftAsync(string? triggerValue, bool alsoLiftWhen = false)
    {
        var original = Environment.GetEnvironmentVariable(TriggerVariable);
        try
        {
            Environment.SetEnvironmentVariable(TriggerVariable, triggerValue);

            var builder = DistributedApplication.CreateBuilder([]);
            var gateway = AddGateway(builder);

            gateway.WithE2eGatewayRateLimitLift(alsoLiftWhen);

            return await EnvironmentOf(builder, gateway.Resource);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TriggerVariable, original);
        }
    }

    // AddResource rather than AddProject: the extension only needs a ProjectResource in the model, and
    // AddProject validates that the project file exists on disk.
    private static IResourceBuilder<ProjectResource> AddGateway(IDistributedApplicationBuilder builder) =>
        builder.AddResource(new ProjectResource("gateway"));

    /// <summary>
    /// Resolves a resource's environment variables in publish mode, where every reference stays a
    /// manifest expression rather than a value DCP happened to allocate.
    /// </summary>
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
