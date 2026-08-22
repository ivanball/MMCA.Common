using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Executable security invariants for
/// <see cref="WebApplicationBuilderExtensions.AddForwardedJwtBearer(IServiceCollection, string, string, IConfiguration, IHostEnvironment, bool?)"/>:
/// the signature algorithm stays pinned to RS256, and <c>RequireHttpsMetadata</c> resolves
/// secure-by-default (explicit argument, then
/// <see cref="WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey"/>, then true everywhere
/// except Development). The registration code is executed for real and the resulting options are
/// read back; nothing is fetched from the authority, so no Identity service is needed.
/// </summary>
public sealed class ForwardedJwtBearerSecurityTests
{
    [Fact]
    public void AddForwardedJwtBearer_PinsTheSignatureAlgorithmToRs256()
    {
        var options = BuildJwtBearerOptions(Environments.Production);

        options.TokenValidationParameters.ValidAlgorithms.Should().BeEquivalentTo(
            [SecurityAlgorithms.RsaSha256],
            "an unpinned validator accepts an HS256 token forged with the RSA public key as the HMAC secret");
    }

    [Fact]
    public void AddForwardedJwtBearer_OutsideDevelopment_RequiresHttpsMetadataByDefault() =>
        BuildJwtBearerOptions(Environments.Production).RequireHttpsMetadata.Should().BeTrue(
            "a deployment that never opted out must not fetch signing keys over plain HTTP");

    [Fact]
    public void AddForwardedJwtBearer_InDevelopment_DoesNotRequireHttpsMetadata() =>
        BuildJwtBearerOptions(Environments.Development).RequireHttpsMetadata.Should().BeFalse(
            "Aspire service-discovery URLs are http:// and no Development host serves metadata over TLS");

    [Fact]
    public void AddForwardedJwtBearer_HonorsTheConfigurationKey_OutsideDevelopment()
    {
        var options = BuildJwtBearerOptions(
            Environments.Production,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey] = "false",
            });

        options.RequireHttpsMetadata.Should().BeFalse(
            "an internal-ingress h2c authority is a legitimate, auditable opt-out through configuration");
    }

    [Fact]
    public void AddForwardedJwtBearer_ExplicitArgument_BeatsTheConfigurationKey()
    {
        var options = BuildJwtBearerOptions(
            Environments.Production,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey] = "false",
            },
            requireHttpsMetadata: true);

        options.RequireHttpsMetadata.Should().BeTrue(
            "the argument is the caller's last word: configuration must not be able to weaken it");
    }

    [Fact]
    public void AddForwardedJwtBearer_WithACleartextAuthorityAndNoOptOut_FailsFast()
    {
        // This is the whole reason the config key exists: the JWT bearer post-configure step refuses a
        // plain-HTTP authority once RequireHttpsMetadata is true, so a host on an internal-ingress h2c
        // authority must opt out deliberately instead of discovering it as a runtime 500.
        var act = () => BuildJwtBearerOptions(Environments.Production, authority: "http://identity");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RequireHttpsMetadata*");
    }

    [Fact]
    public void AddForwardedJwtBearer_WhenOptedOutOutsideDevelopment_RegistersTheStartupWarning()
    {
        var services = BuildServices(
            Environments.Production,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey] = "false",
            });

        services.Should().ContainSingle(
            d => d.ServiceType.FullName == "Microsoft.AspNetCore.Hosting.IStartupFilter",
            "the deliberate opt-out must announce itself once at startup, not silently");
    }

    [Fact]
    public void AddForwardedJwtBearer_InDevelopment_RegistersNoStartupWarning() =>
        BuildServices(Environments.Development).Should().NotContain(
            d => d.ServiceType.FullName == "Microsoft.AspNetCore.Hosting.IStartupFilter",
            "plain-HTTP metadata is the expected Development posture, so warning about it would be noise");

    private static ServiceCollection BuildServices(
        string environmentName,
        Dictionary<string, string?>? configurationValues = null,
        bool? requireHttpsMetadata = null,
        string authority = "https://identity")
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>(StringComparer.Ordinal))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // DefaultAuthenticationConfigurationProvider resolves IConfiguration from the container.
        services.AddSingleton(configuration);

        services.AddForwardedJwtBearer(
            authority,
            "mmca-api",
            configuration,
            new StubHostEnvironment { EnvironmentName = environmentName },
            requireHttpsMetadata);

        return services;
    }

    private static JwtBearerOptions BuildJwtBearerOptions(
        string environmentName,
        Dictionary<string, string?>? configurationValues = null,
        bool? requireHttpsMetadata = null,
        string authority = "https://identity")
    {
        ServiceProvider provider = BuildServices(environmentName, configurationValues, requireHttpsMetadata, authority)
            .BuildServiceProvider();

        return provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MMCA.Common.API.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
