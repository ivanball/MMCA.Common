using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// Executable security invariants for <see cref="GatewayCorsExtensions.AddCommonGatewayCors"/>: the
/// gateway's default policy is credentialed with an explicit origin list outside Development, and the
/// permissive Development policy never carries credentials. AllowAnyOrigin and AllowCredentials are
/// the pair browsers reject and that would expose credentialed cross-origin calls, so the split is
/// asserted rather than left to review.
/// </summary>
public sealed class GatewayCorsExtensionsTests
{
    [Fact]
    public void AddCommonGatewayCors_OutsideDevelopment_RestrictsOriginsAndAllowsCredentials()
    {
        string[] expectedOrigins = ["https://app.example.com", "https://admin.example.com"];

        CorsPolicy policy = BuildDefaultPolicy(Environments.Production, expectedOrigins);

        policy.AllowAnyOrigin.Should().BeFalse(
            "a credentialed policy must never widen to any origin");
        policy.Origins.Should().BeEquivalentTo(expectedOrigins);
        policy.SupportsCredentials.Should().BeTrue(
            "the gateway fronts cookie- and Authorization-header traffic");
    }

    [Fact]
    public void AddCommonGatewayCors_OutsideDevelopment_WithNoConfiguredOrigins_FailsClosed()
    {
        CorsPolicy policy = BuildDefaultPolicy(Environments.Production);

        policy.AllowAnyOrigin.Should().BeFalse();
        policy.Origins.Should().BeEmpty(
            "a missing Cors:AllowedOrigins section must close the gateway, not open it");
    }

    [Fact]
    public void AddCommonGatewayCors_InDevelopment_AllowsAnyOriginWithoutCredentials()
    {
        CorsPolicy policy = BuildDefaultPolicy(Environments.Development);

        policy.AllowAnyOrigin.Should().BeTrue();
        policy.SupportsCredentials.Should().BeFalse(
            "the permissive Development policy must never be combined with credentials");
    }

    private static CorsPolicy BuildDefaultPolicy(string environmentName, params string[] allowedOrigins)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < allowedOrigins.Length; i++)
        {
            values["Cors:AllowedOrigins:" + i.ToString(CultureInfo.InvariantCulture)] = allowedOrigins[i];
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddCommonGatewayCors(
            configuration,
            new StubHostEnvironment { EnvironmentName = environmentName });

        ServiceProvider provider = services.BuildServiceProvider();
        CorsOptions corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        CorsPolicy? policy = corsOptions.GetPolicy(corsOptions.DefaultPolicyName);

        policy.Should().NotBeNull("AddCommonGatewayCors registers the DEFAULT policy");
        return policy;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MMCA.Common.Aspire.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
