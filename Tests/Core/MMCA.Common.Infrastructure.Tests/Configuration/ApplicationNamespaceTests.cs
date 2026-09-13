using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Caching;
using MMCA.Common.Infrastructure.Configuration;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Configuration;

/// <summary>
/// SEC-Common-53: shared infrastructure (Redis cache keys, distributed locks, the SignalR backplane
/// and broker queues) must be namespaced per application by DEFAULT, because the framework ships the
/// hub and the consumer types that would otherwise derive identical names in every consumer.
/// </summary>
public sealed class ApplicationNamespaceTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static IHostEnvironment Environment(string applicationName)
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.ApplicationName).Returns(applicationName);
        return mock.Object;
    }

    [Fact]
    public void Resolve_PrefersTheExplicitConfigurationKey()
    {
        var resolved = ApplicationNamespace.Resolve(
            Config((ApplicationNamespace.ConfigurationKey, "shared-ns")),
            Environment("MMCA.Demo.Gateway"));

        resolved.Should().Be("shared-ns");
    }

    [Fact]
    public void Resolve_FallsBackToTheApplicationName()
    {
        var resolved = ApplicationNamespace.Resolve(Config(), Environment("MMCA.Demo.Gateway"));

        resolved.Should().Be("MMCA-Demo-Gateway");
    }

    [Fact]
    public void Resolve_ReadsTheHostApplicationNameKeyWhenNoEnvironmentIsAvailable()
    {
        var resolved = ApplicationNamespace.Resolve(Config(("applicationName", "MMCA.Demo.Sales")), environment: null);

        resolved.Should().Be("MMCA-Demo-Sales");
    }

    [Fact]
    public void Resolve_NeverReturnsEmpty()
    {
        ApplicationNamespace.Resolve(configuration: null, environment: null).Should().Be(ApplicationNamespace.Fallback);
        ApplicationNamespace.Resolve(Config(), Environment("   ")).Should().Be(ApplicationNamespace.Fallback);
        ApplicationNamespace.Resolve(Config(), Environment("///")).Should().Be(ApplicationNamespace.Fallback);
    }

    [Fact]
    public void Resolve_IsDistinctPerApplication()
    {
        var gateway = ApplicationNamespace.Resolve(Config(), Environment("MMCA.Demo.Gateway"));
        var other = ApplicationNamespace.Resolve(Config(), Environment("MMCA.Other.Gateway"));

        gateway.Should().NotBe(other);
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(configuration);
        services.AddSingleton(Environment("MMCA.Demo.Catalog"));
        services.Configure<CacheKeyPrefixOptions>(configuration.GetSection(CacheKeyPrefixOptions.SectionName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void CacheKeyNamespace_DefaultsToTheApplicationNamespaceWhenNoPrefixIsConfigured()
    {
        using var provider = BuildProvider(Config());

        var keyNamespace = CacheKeyNamespace.From(provider);

        keyNamespace.Qualify("products").Should().Be("MMCA-Demo-Catalog:products");
    }

    [Fact]
    public void CacheKeyNamespace_KeepsAnExplicitlyConfiguredPrefix()
    {
        using var provider = BuildProvider(Config(("Cache:KeyPrefix", "conference:")));

        var keyNamespace = CacheKeyNamespace.From(provider);

        keyNamespace.Qualify("products").Should().Be("conference:products");
    }
}
