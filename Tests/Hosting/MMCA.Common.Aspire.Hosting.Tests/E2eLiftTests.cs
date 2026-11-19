using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using AwesomeAssertions;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.Aspire.Gateway;
using MMCA.Common.Gateway;
using static MMCA.Common.Aspire.Hosting.Tests.Support.PublishModeEnvironment;

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
/// <para>
/// Both lifts also fire on <c>E2E_FORCE_WASM</c> (item 25, rule 2): a forced-WebAssembly run implies
/// the same request volume, and the helpers now read that variable themselves.
/// </para>
/// </summary>
[Collection(E2eEnvironmentCollection.Name)]
public sealed class E2eLiftTests
{
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
    public async Task WithE2eGatewayRateLimitLift_WhenOnlyForceWasmIsSet_SetsAllThreeOverrides()
    {
        // The OR disjunct that used to be a call-site flag: a forced-WebAssembly run lifts the limits
        // without a second workflow variable.
        Dictionary<string, string> environment = await LiftAsync(triggerValue: null, forceWasmValue: "true");

        environment.Should().ContainKey("GatewayRateLimiting__PermitLimit");
        environment.Should().ContainKey("GatewayRateLimiting__GlobalConcurrencyLimit");
        environment.Should().ContainKey("MmcaGateway__RateLimiterPolicies__auth-tight__PermitLimit");
    }

    [Fact]
    public async Task WithE2eGatewayRateLimitLift_WhenForceWasmSaysFalse_WritesNothing()
    {
        Dictionary<string, string> environment = await LiftAsync(triggerValue: null, forceWasmValue: "false");

        environment.Should().NotContainKey("GatewayRateLimiting__PermitLimit");
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
        using var scope = new E2eEnvironmentScope(forceWasm: "true");
        var builder = DistributedApplication.CreateBuilder([]);
        var gateway = AddGateway(builder);

        gateway.WithE2eGatewayRateLimitLift().Should().BeSameAs(gateway);
    }

    [Fact]
    public async Task WithE2eRegistrationThrottleLift_SharesTheGatewayLiftsTrigger()
    {
        // Both lifts exist because of the same single-loopback-IP E2E suite, so they must not be able
        // to drift onto different switches: one workflow variable turns both on.
        Dictionary<string, string> environment = await RegistrationLiftAsync(liftValue: "true", forceWasmValue: null);

        environment.Should().ContainKey("LoginProtection__MaxRegistrationsPerIpPerHour")
            .WhoseValue.Should().Be("1000");
    }

    [Fact]
    public async Task WithE2eRegistrationThrottleLift_WhenOnlyForceWasmIsSet_Lifts()
    {
        Dictionary<string, string> environment = await RegistrationLiftAsync(liftValue: null, forceWasmValue: "true");

        environment.Should().ContainKey("LoginProtection__MaxRegistrationsPerIpPerHour")
            .WhoseValue.Should().Be("1000");
    }

    [Fact]
    public async Task WithE2eRegistrationThrottleLift_WhenNeitherTriggerFires_WritesNothing()
    {
        Dictionary<string, string> environment = await RegistrationLiftAsync(liftValue: null, forceWasmValue: null);

        environment.Should().NotContainKey("LoginProtection__MaxRegistrationsPerIpPerHour");
        environment.Should().NotContainKey("RateLimiting__AuthIpPermitLimit");
    }

    [Theory]
    [InlineData("true", null)]
    [InlineData(null, "true")]
    public async Task WithE2eRegistrationThrottleLift_WhenEitherTriggerFires_LiftsTheAuthIpLimiter(
        string? liftValue,
        string? forceWasmValue)
    {
        // The suite arrives from one loopback IP and the passkey autofill options call spends an
        // auth-ip permit on every login-page visit, so the same trigger lifts that limiter too.
        Dictionary<string, string> environment = await RegistrationLiftAsync(liftValue, forceWasmValue);

        environment.Should().ContainKey("RateLimiting__AuthIpPermitLimit")
            .WhoseValue.Should().Be("100000");
    }

    [Fact]
    public async Task WithE2eRegistrationThrottleLift_WhenTheEnvironmentSaysFalse_LeavesTheAuthIpLimiter()
    {
        Dictionary<string, string> environment = await RegistrationLiftAsync(liftValue: "false", forceWasmValue: "false");

        environment.Should().NotContainKey("RateLimiting__AuthIpPermitLimit");
    }

    [Fact]
    public async Task WithE2eRegistrationThrottleLift_EmitsTheAuthIpKeyDerivedFromTheCommonOwnedSettings()
    {
        Dictionary<string, string> environment = await RegistrationLiftAsync(liftValue: "true", forceWasmValue: null);

        environment.Should().ContainKey(
            $"{RateLimitingSettings.SectionName}__{nameof(RateLimitingSettings.AuthIpPermitLimit)}");
    }

    private static async Task<Dictionary<string, string>> LiftAsync(string? triggerValue, string? forceWasmValue = null)
    {
        using var scope = new E2eEnvironmentScope(lift: triggerValue, forceWasm: forceWasmValue);

        var builder = DistributedApplication.CreateBuilder([]);
        var gateway = AddGateway(builder);

        gateway.WithE2eGatewayRateLimitLift();

        return await EnvironmentOf(builder, gateway.Resource);
    }

    private static async Task<Dictionary<string, string>> RegistrationLiftAsync(string? liftValue, string? forceWasmValue)
    {
        using var scope = new E2eEnvironmentScope(lift: liftValue, forceWasm: forceWasmValue);

        var builder = DistributedApplication.CreateBuilder([]);
        var identity = builder.AddResource(new DotnetProjectResource("identity", "."));
        identity.WithE2eRegistrationThrottleLift();

        return await EnvironmentOf(builder, identity.Resource);
    }

    // AddResource rather than AddDotnetProject: the extension only needs a DotnetProjectResource in the model, and
    // AddDotnetProject validates that the project file exists on disk.
    private static IResourceBuilder<DotnetProjectResource> AddGateway(IDistributedApplicationBuilder builder) =>
        builder.AddResource(new DotnetProjectResource("gateway", "."));
}
