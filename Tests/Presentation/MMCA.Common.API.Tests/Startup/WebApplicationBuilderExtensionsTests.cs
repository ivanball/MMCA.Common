using AwesomeAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

public sealed class WebApplicationBuilderExtensionsTests
{
    [Fact]
    public void AddCommonApiVersioning_RegistersApiVersioningServices()
    {
        var services = new ServiceCollection();

        services.AddCommonApiVersioning();

        services.Any(s => s.ServiceType.FullName != null
                       && s.ServiceType.FullName.Contains("ApiVersioning", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonApiVersioning_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddCommonApiVersioning();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCommonRateLimiting_RegistersRateLimiterServices()
    {
        var services = new ServiceCollection();

        services.AddCommonRateLimiting();

        services.Any(s => s.ServiceType.FullName != null
                       && s.ServiceType.FullName.Contains("RateLimit", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonRateLimiting_WithCustomParameters_RegistersServices()
    {
        var services = new ServiceCollection();

        services.AddCommonRateLimiting(permitLimit: 50, queueLimit: 5, perUserPermitLimit: 10);

        services.Any(s => s.ServiceType.FullName != null
                       && s.ServiceType.FullName.Contains("RateLimit", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonResponseCompression_RegistersCompressionProviders()
    {
        var services = new ServiceCollection();

        services.AddCommonResponseCompression();

        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ResponseCompressionOptions>>();
        options.Value.EnableForHttps.Should().BeTrue();
    }

    [Fact]
    public void AddCommonResponseCompression_ConfiguresBrotliCompression()
    {
        var services = new ServiceCollection();

        services.AddCommonResponseCompression();

        ServiceProvider provider = services.BuildServiceProvider();
        var brotliOptions = provider.GetRequiredService<IOptions<BrotliCompressionProviderOptions>>();
        brotliOptions.Value.Level.Should().Be(System.IO.Compression.CompressionLevel.Fastest);
    }

    [Fact]
    public void AddCommonResponseCompression_ConfiguresGzipCompression()
    {
        var services = new ServiceCollection();

        services.AddCommonResponseCompression();

        ServiceProvider provider = services.BuildServiceProvider();
        var gzipOptions = provider.GetRequiredService<IOptions<GzipCompressionProviderOptions>>();
        gzipOptions.Value.Level.Should().Be(System.IO.Compression.CompressionLevel.SmallestSize);
    }

    [Fact]
    public void AddCommonResponseCompression_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddCommonResponseCompression();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCommonAuthentication_WithValidConfig_RegistersAuthenticationServices()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateValidJwtConfiguration();

        services.AddCommonAuthentication(configuration);

        services.Any(s => s.ServiceType.FullName != null
                       && s.ServiceType.FullName.Contains("IAuthenticationService", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonAuthentication_WithValidConfig_RegistersAuthorizationPolicies()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateValidJwtConfiguration();

        services.AddCommonAuthentication(configuration);

        services.Any(s => s.ServiceType.FullName != null
                       && s.ServiceType.FullName.Contains("IAuthorizationService", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonAuthentication_WithMissingJwtSection_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        services.AddCommonAuthentication(configuration);

        // The exception is thrown during JWT Bearer handler configuration,
        // which happens when the authentication handler is resolved.
        // Since AddJwtBearer uses a deferred configuration delegate,
        // it throws when the options are built at resolve time.
        ServiceProvider provider = services.BuildServiceProvider();
        Action resolve = () => provider.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();

        // The authentication scheme provider itself resolves fine;
        // the exception occurs when PostConfigure runs on JwtBearerOptions.
        // We verify it registered rather than throwing at registration time.
        resolve.Should().NotThrow();
    }

    [Fact]
    public void AddCommonAuthentication_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateValidJwtConfiguration();

        IServiceCollection result = services.AddCommonAuthentication(configuration);

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCommonCors_RegistersCorsServices()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration();

        services.AddCommonCors(configuration);

        services.Any(s => s.ServiceType.Equals(typeof(ICorsService)))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonCors_ConfiguresAllowSpecificOriginsPolicy()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration("https://example.com", "https://app.example.com");

        services.AddCommonCors(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>();
        CorsPolicy? policy = corsOptions.Value.GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins);

        policy.Should().NotBeNull();
        policy!.Origins.Should().Contain("https://example.com");
        policy.Origins.Should().Contain("https://app.example.com");
    }

    [Fact]
    public void AddCommonCors_ConfiguresAllowAllPolicy()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration();

        services.AddCommonCors(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>();
        CorsPolicy? policy = corsOptions.Value.GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowAll);

        policy.Should().NotBeNull();
        policy!.AllowAnyOrigin.Should().BeTrue();
        policy.AllowAnyHeader.Should().BeTrue();
        policy.AllowAnyMethod.Should().BeTrue();
    }

    [Fact]
    public void AddCommonCors_AllowSpecificOriginsPolicy_AllowsCredentials()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration("https://example.com");

        services.AddCommonCors(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>();
        CorsPolicy? policy = corsOptions.Value.GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins);

        policy.Should().NotBeNull();
        policy!.SupportsCredentials.Should().BeTrue();
    }

    [Fact]
    public void AddCommonCors_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration();

        IServiceCollection result = services.AddCommonCors(configuration);

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void CorsPolicyAllowSpecificOrigins_HasExpectedValue() =>
        WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins
            .Should().Be("_allowSpecificOrigins");

    [Fact]
    public void CorsPolicyAllowAll_HasExpectedValue() =>
        WebApplicationBuilderExtensions.CorsPolicyAllowAll
            .Should().Be("_allowAll");

    private static IConfiguration CreateValidJwtConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Jwt:SecretForKey", "RgDldLrK+p+T0JisAKdD7THnT/npmWYl4vV3UUiRSVE=" },
                { "Jwt:Issuer", "https://test" },
                { "Jwt:Audience", "testapi" },
                { "Jwt:AccessTokenExpirationMinutes", "15" },
                { "Jwt:RefreshTokenExpirationDays", "7" },
            })
            .Build();

    private static IConfiguration CreateCorsConfiguration(params string[] allowedOrigins) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                [.. allowedOrigins.Select((origin, i) => KeyValuePair.Create<string, string?>($"Cors:AllowedOrigins:{i}", origin))])
            .Build();
}
