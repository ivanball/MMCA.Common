using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EntityConfigurationOptionsTests
{
    [Fact]
    public void AdditionalAssemblies_DefaultsToEmptyList()
    {
        var sut = new EntityConfigurationOptions();

        sut.AdditionalAssemblies.Should().BeEmpty();
    }

    [Fact]
    public void AdditionalAssemblies_CanAddAssemblies()
    {
        var sut = new EntityConfigurationOptions();
        var assembly = typeof(EntityConfigurationOptionsTests).Assembly;

        sut.AdditionalAssemblies.Add(assembly);

        sut.AdditionalAssemblies.Should().ContainSingle();
        sut.AdditionalAssemblies[0].Should().BeSameAs(assembly);
    }

    [Fact]
    public void AdditionalAssemblies_CanAddMultipleAssemblies()
    {
        var sut = new EntityConfigurationOptions();
        var assembly1 = typeof(EntityConfigurationOptionsTests).Assembly;
        var assembly2 = typeof(object).Assembly;

        sut.AdditionalAssemblies.Add(assembly1);
        sut.AdditionalAssemblies.Add(assembly2);

        sut.AdditionalAssemblies.Should().HaveCount(2);
    }
}
