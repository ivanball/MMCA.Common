using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using MMCA.Common.Testing.Support;
using Xunit;

namespace MMCA.Common.Testing.Tests.Support;

/// <summary>
/// The helper's docstring promises it overrides feature-flag values from <c>appsettings.json</c>,
/// i.e. that everything else survives. It used to register a flags-only <see cref="IConfiguration"/>,
/// and .NET DI hands a non-collection dependency the LAST registration, so every component built
/// afterwards that injects <see cref="IConfiguration"/> directly (a connection-string reader, a JWT
/// authority check) got a root with nothing in it but the flags.
/// </summary>
public sealed class FeatureManagementTestExtensionsTests
{
    [Fact]
    public void ConfigureTestFeatureFlags_LayersTheFlagsOnTopOfTheHostConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(HostConfiguration());

        services.ConfigureTestFeatureFlags(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["New"] = true,
            ["Old"] = false,
        });

        var configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();

        configuration["ConnectionStrings:Default"].Should().Be(
            "Server=host;Database=app",
            "the host's own configuration must survive the flag override");
        configuration["Authentication:JwtBearer:Authority"].Should().Be("https://issuer.test");
        configuration["FeatureManagement:New"].Should().Be("True");
        configuration["FeatureManagement:Old"].Should().Be(
            "False",
            "the in-memory source is added last, so it wins over the host's value");
    }

    [Fact]
    public async Task ConfigureTestFeatureFlags_DrivesTheFeatureManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(HostConfiguration());
        services.AddLogging();

        services.ConfigureTestFeatureFlags(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["New"] = true,
            ["Old"] = false,
        });

        var featureManager = services.BuildServiceProvider().GetRequiredService<IFeatureManager>();

        (await featureManager.IsEnabledAsync("New")).Should().BeTrue();
        (await featureManager.IsEnabledAsync("Old")).Should().BeFalse();
    }

    [Fact]
    public void ConfigureTestFeatureFlags_WithNoHostConfiguration_StillRegistersTheFlags()
    {
        var services = new ServiceCollection();

        services.ConfigureTestFeatureFlags(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["New"] = true,
        });

        var configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();

        configuration["FeatureManagement:New"].Should().Be("True");
    }

    private static IConfiguration HostConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = "Server=host;Database=app",
                ["Authentication:JwtBearer:Authority"] = "https://issuer.test",
                ["FeatureManagement:Old"] = "true",
            })
            .Build();
}
