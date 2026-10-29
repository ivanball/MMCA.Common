using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Testing.Conformance;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance;

/// <summary>
/// Self-tests for <see cref="TenantResolutionTestsBase"/>: the base declares its three gates, a
/// compliant configuration passes each of them, and each non-compliant configuration makes the
/// matching gate throw. The probe subclass is private, so xUnit never collects its inherited
/// facts; the tests call those facts directly.
/// </summary>
public sealed class TenantResolutionTestsBaseTests
{
    private static readonly string[] ExpectedGates =
    [
        nameof(TenantResolutionTestsBase.Header_resolution_is_an_explicit_opt_in),
        nameof(TenantResolutionTestsBase.Claim_is_tried_before_header),
        nameof(TenantResolutionTestsBase.Health_endpoints_are_excluded),
    ];

    [Fact]
    public void Base_DeclaresEveryGate_AsAFact()
    {
        var facts = typeof(TenantResolutionTestsBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name);

        facts.Should().BeEquivalentTo(
            ExpectedGates,
            "every gate the base ships runs in each consumer's sealed subclass; dropping one silently stops it running everywhere");
    }

    [Fact]
    public void Base_DoesNotAllowHeaderResolutionByDefault() =>
        new Probe(new TenancySettings { Enabled = true }).ExposedAllowsHeaderResolution.Should().BeFalse(
            "a consumer must opt in to header resolution explicitly, by overriding AllowsHeaderResolution");

    // -- Compliant configurations pass every gate --
    [Fact]
    public void CompliantDefaults_PassAllThreeGates()
    {
        var sut = new Probe(new TenancySettings { Enabled = true });

        AssertPassesAll(sut);
    }

    [Fact]
    public void DisabledTenancyWithAHeaderFirstOrder_PassesTheOptInGate()
    {
        var settings = new TenancySettings { Enabled = false };
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        var sut = new Probe(settings);

        Action act = sut.Header_resolution_is_an_explicit_opt_in;

        act.Should().NotThrow("a host with tenancy off resolves nothing, so its order cannot expose a header");
    }

    [Fact]
    public void ExplicitlyAllowedClaimThenHeader_PassesAllThreeGates()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Claim);
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        var sut = new Probe(settings, allowsHeaderResolution: true);

        AssertPassesAll(sut);
    }

    [Fact]
    public void CustomExclusionsThatKeepTheProbes_PassTheHealthGate()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ExcludedPathPrefixes.Add("/health");
        settings.ExcludedPathPrefixes.Add("/alive");
        settings.ExcludedPathPrefixes.Add("/ping");
        var sut = new Probe(settings);

        Action act = sut.Health_endpoints_are_excluded;

        act.Should().NotThrow();
    }

    // -- Each non-compliant configuration fails the matching gate --
    [Fact]
    public void HeaderInTheOrderWithoutTheOptIn_FailsTheOptInGate()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Claim);
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        var sut = new Probe(settings);

        Action act = sut.Header_resolution_is_an_explicit_opt_in;

        act.Should().Throw<Exception>("a header the consumer did not explicitly allow lets any caller name any tenant");
    }

    [Fact]
    public void HeaderOnlyOrderWithoutTheOptIn_FailsTheOptInGate()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        var sut = new Probe(settings);

        Action act = sut.Header_resolution_is_an_explicit_opt_in;

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void HeaderBeforeClaim_FailsTheOrderingGate()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Claim);
        var sut = new Probe(settings, allowsHeaderResolution: true);

        Action act = sut.Claim_is_tried_before_header;

        act.Should().Throw<Exception>("the signed claim must win over the caller-supplied header");
    }

    [Fact]
    public void ExclusionsMissingHealth_FailTheHealthGate()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ExcludedPathPrefixes.Add("/alive");
        var sut = new Probe(settings);

        Action act = sut.Health_endpoints_are_excluded;

        act.Should().Throw<Exception>("a probe rejected for having no tenant takes the replica out of rotation");
    }

    [Fact]
    public void ExclusionsMissingAlive_FailTheHealthGate()
    {
        var settings = new TenancySettings { Enabled = true };
        settings.ExcludedPathPrefixes.Add("/health");
        var sut = new Probe(settings);

        Action act = sut.Health_endpoints_are_excluded;

        act.Should().Throw<Exception>();
    }

    private static void AssertPassesAll(Probe sut)
    {
        Action optIn = sut.Header_resolution_is_an_explicit_opt_in;
        Action ordering = sut.Claim_is_tried_before_header;
        Action health = sut.Health_endpoints_are_excluded;

        optIn.Should().NotThrow();
        ordering.Should().NotThrow();
        health.Should().NotThrow();
    }

    /// <summary>A configurable subclass; private, so xUnit does not collect its inherited facts.</summary>
    private sealed class Probe(TenancySettings settings, bool allowsHeaderResolution = false) : TenantResolutionTestsBase
    {
        public bool ExposedAllowsHeaderResolution => base.AllowsHeaderResolution;

        protected override TenancySettings Settings { get; } = settings;

        protected override bool AllowsHeaderResolution => allowsHeaderResolution;
    }
}
