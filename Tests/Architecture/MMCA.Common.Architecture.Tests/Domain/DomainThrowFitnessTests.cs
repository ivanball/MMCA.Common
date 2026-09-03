using MMCA.Common.Architecture.Tests.DomainThrowFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Self-test for the domain-throw rule shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="DomainThrowTestsBase"/>). A rule that reads IL cannot be verified by reading it, so
/// this points a map at THIS assembly, which compiles the offending and the innocent throw sites side
/// by side in <c>DomainThrowFixtures</c>, and pins every behaviour: business exceptions are caught
/// (framework-named or custom), the three argument guards are not, a bare rethrow is not, a throw of
/// a value built elsewhere is reported as UNVERIFIABLE, and the allowlist silences exactly what it
/// names. A final test runs the rule against MMCA.Common's own Domain, which ADR-013 says holds
/// exactly one throw: an argument guard.
/// </summary>
public sealed class DomainThrowFitnessTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.DomainThrowFixtures";

    /// <summary>
    /// The throws in this test assembly that are not fixtures: <c>NavigationContractTests</c> guards
    /// its embedded resource with an <c>InvalidOperationException</c>, and the OpenAPI XML-comment
    /// source generator emits a transformer into every assembly that references the API layer. The map
    /// below scans the whole test assembly, so allowlisting both keeps the fixtures the only subject
    /// of these assertions. This is the allowlist doing its day job: generated plumbing is exactly
    /// what it is for.
    /// </summary>
    private static readonly string[] NonFixtureThrows =
    [
        "MMCA.Common.Architecture.Tests.Ui.NavigationContractTests",
        "Microsoft.AspNetCore.OpenApi.Generated",
    ];

    private readonly FixtureAssemblyMap _map = new();

    [Fact]
    public void BusinessException_IsFlagged()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, NonFixtureThrows);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(nameof(InvalidOperationThrowingFixture));
        message.Should().Contain("System.InvalidOperationException", "the report must name the exception thrown");
    }

    [Fact]
    public void CustomDomainException_IsAlsoFlagged()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, NonFixtureThrows);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                nameof(CustomExceptionThrowingFixture),
                "a custom exception is the same defect wearing a domain name");
    }

    [Fact]
    public void ArgumentGuards_AreNotFlagged()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, NonFixtureThrows);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(ArgumentGuardFixture),
                "ArgumentException, ArgumentNullException and ArgumentOutOfRangeException report a caller bug, not a business outcome");
    }

    [Fact]
    public void BareRethrow_IsNotFlagged()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, NonFixtureThrows);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(RethrowingFixture),
                "a bare throw; compiles to the rethrow opcode, so preserving a caught exception stays free");
    }

    [Fact]
    public void NonThrowingCode_IsNotFlagged()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, NonFixtureThrows);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(
                nameof(NonThrowingFixture),
                "returning a value instead of throwing is exactly what the rule protects");
    }

    [Fact]
    public void ThrowOfAValueBuiltElsewhere_IsReportedAsUnverifiable()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, NonFixtureThrows);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("UNVERIFIABLE", "a throw whose exception type is not knowable is neither passed nor failed");
        message.Should().Contain(nameof(IndirectThrowFixture), "the blind spot must name the method that owns it");
    }

    [Fact]
    public void AllowlistedNamespace_SilencesTheRule()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(
            _map,
            [.. NonFixtureThrows, FixtureNamespace]);

        act.Should().NotThrow(
            "a namespace entry covers the throwing types under it, which is how a repo records the plumbing it accepts");
    }

    [Fact]
    public void AllowlistedType_SilencesOnlyThatType()
    {
        var allowed = $"{FixtureNamespace}.{nameof(InvalidOperationThrowingFixture)}";

        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(_map, [.. NonFixtureThrows, allowed]);

        var message = act.Should().Throw<XunitException>(
            "the custom-exception fixture is still outside the allowlist").Which.Message;

        message.Should().NotContain(nameof(InvalidOperationThrowingFixture));
        message.Should().Contain(nameof(CustomExceptionThrowingFixture));
    }

    [Fact]
    public void MMCACommonDomain_HoldsOnlyArgumentGuards()
    {
        var act = () => ArchitectureRules.DomainThrowsOnlyArgumentGuards(new CommonArchitectureMap(), []);

        act.Should().NotThrow(
            "MMCA.Common's own Domain is the reference implementation of ADR-013: its single throw is the ArgumentNullException guard in Specification.cs");
    }

    /// <summary>A map whose single Domain layer is this test assembly, so the rule scans the fixtures above.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Domain, typeof(NonThrowingFixture).Assembly),
        ];
    }
}
