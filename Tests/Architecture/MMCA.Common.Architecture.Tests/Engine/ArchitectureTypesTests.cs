using MMCA.Common.Architecture.Tests.DependencyScanFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Engine;

/// <summary>
/// Pins what <see cref="ArchitectureTypes"/> counts as a dependency, against fixtures whose only
/// reference to the target namespace is known: a lambda body, an async body, an attribute and a
/// generic argument. Every layer rule in the package rests on this scan, so a fixture that stops
/// failing means the rules have started passing vacuously.
/// </summary>
public sealed class ArchitectureTypesTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.DependencyScanFixtures";

    private const string TargetNamespace = FixtureNamespace + ".Targets";

    [Theory]
    [InlineData(nameof(LambdaOnlyDependent))]
    [InlineData(nameof(AsyncOnlyDependent))]
    [InlineData(nameof(AttributeOnlyDependent))]
    [InlineData(nameof(GenericArgumentOnlyDependent))]
    public void ADependencyNamedOnlyThere_IsFound(string fixture)
    {
        var result = Fixture(fixture).ShouldNot().HaveDependencyOnAny(TargetNamespace);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Equal($"{FixtureNamespace}.{fixture}");
    }

    [Fact]
    public void ATypeNamingNoTarget_Passes()
    {
        var selection = Fixture(nameof(IndependentSubject));

        selection.GetTypeNames().Should().Equal($"{FixtureNamespace}.{nameof(IndependentSubject)}");
        selection.ShouldNot().HaveDependencyOnAny(TargetNamespace).IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void ATypeFullName_MatchesThatTypeOnly()
    {
        Fixture(nameof(AttributeOnlyDependent))
            .Should().HaveDependencyOnAll($"{TargetNamespace}.AttributeTargetAttribute")
            .IsSuccessful.Should().BeTrue();

        Fixture(nameof(AttributeOnlyDependent))
            .ShouldNot().HaveDependencyOnAny($"{TargetNamespace}.LambdaTarget")
            .IsSuccessful.Should().BeTrue();
    }

    [Theory]
    [InlineData(TargetNamespace + ".")]
    [InlineData(FixtureNamespace + ".Targ")]
    public void ANameThatIsNotAWholeSegmentRun_MatchesNothing(string dependency) =>
        Fixture(nameof(AttributeOnlyDependent))
            .ShouldNot().HaveDependencyOnAny(dependency)
            .IsSuccessful.Should().BeTrue();

    [Fact]
    public void CompilerGeneratedTypes_AreNotSubjects() =>
        ArchitectureTypes.InAssembly(typeof(LambdaOnlyDependent).Assembly).GetTypeNames()
            .Where(name => name.StartsWith(FixtureNamespace + ".", StringComparison.Ordinal))
            .Should().NotContain(name => name.Contains('<', StringComparison.Ordinal));

    private static ArchitectureTypes Fixture(string name) =>
        ArchitectureTypes.InAssembly(typeof(LambdaOnlyDependent).Assembly).That().HaveName(name);
}
