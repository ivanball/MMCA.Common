using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Persistence;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DefaultEntityConfigurationAssemblyProviderTests
{
    // ── Returns assemblies filtered by .Infrastructure suffix ──
    [Fact]
    public void GetConfigurationAssemblies_ExcludesCommonInfrastructure()
    {
        var options = Options.Create(new EntityConfigurationOptions());
        var sut = new DefaultEntityConfigurationAssemblyProvider(options);

        IReadOnlyList<Assembly> assemblies = sut.GetConfigurationAssemblies();

        // Common.Infrastructure should be excluded by the filter
        foreach (Assembly assembly in assemblies)
        {
            assembly.FullName?.Should().NotContainEquivalentOf("Common.Infrastructure");
        }
    }

    // ── Includes additional assemblies ──
    [Fact]
    public void GetConfigurationAssemblies_IncludesAdditionalAssemblies()
    {
        var additionalAssembly = typeof(DefaultEntityConfigurationAssemblyProviderTests).Assembly;
        var entityConfigOptions = new EntityConfigurationOptions();
        entityConfigOptions.AdditionalAssemblies.Add(additionalAssembly);
        var options = Options.Create(entityConfigOptions);

        var sut = new DefaultEntityConfigurationAssemblyProvider(options);

        IReadOnlyList<Assembly> assemblies = sut.GetConfigurationAssemblies();

        assemblies.Should().Contain(additionalAssembly);
    }

    // ── Deduplicates additional assemblies ──
    [Fact]
    public void GetConfigurationAssemblies_DeduplicatesAdditionalAssemblies()
    {
        var assembly = typeof(DefaultEntityConfigurationAssemblyProviderTests).Assembly;
        var entityConfigOptions = new EntityConfigurationOptions();
        entityConfigOptions.AdditionalAssemblies.Add(assembly);
        entityConfigOptions.AdditionalAssemblies.Add(assembly);
        var options = Options.Create(entityConfigOptions);

        var sut = new DefaultEntityConfigurationAssemblyProvider(options);

        IReadOnlyList<Assembly> assemblies = sut.GetConfigurationAssemblies();

        int count = assemblies.Count(a => a == assembly);
        count.Should().Be(1);
    }

    // ── Empty additional assemblies ──
    [Fact]
    public void GetConfigurationAssemblies_WithNoAdditionalAssemblies_ReturnsOnlyDiscovered()
    {
        var options = Options.Create(new EntityConfigurationOptions());
        var sut = new DefaultEntityConfigurationAssemblyProvider(options);

        IReadOnlyList<Assembly> assemblies = sut.GetConfigurationAssemblies();

        // Should not throw and should return a valid list
        assemblies.Should().NotBeNull();
    }
}
