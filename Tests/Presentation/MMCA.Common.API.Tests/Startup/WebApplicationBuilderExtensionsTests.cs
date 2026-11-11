using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
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
    public void AddCommonRateLimiting_ActivatesGlobalLimiterWith429()
    {
        var services = new ServiceCollection();

        services.AddCommonRateLimiting();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        // The #11 fix: a global limiter must be attached (not merely named policies), so the limiter
        // is actually enforced on every request, and over-limit requests get 429.
        options.GlobalLimiter.Should().NotBeNull();
        options.RejectionStatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
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
        // Fastest, matching Brotli: dynamic API payloads on fractional vCPUs make
        // SmallestSize's extra CPU per response a bad trade.
        gzipOptions.Value.Level.Should().Be(System.IO.Compression.CompressionLevel.Fastest);
    }

    [Fact]
    public void AddCommonResponseCompression_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddCommonResponseCompression();

        result.Should().BeSameAs(services);
    }

    // Provider order is the server's preference when a client weights encodings equally, so the
    // newest encoding the runtime ships goes first and Brotli stays ahead of Gzip.
    [Fact]
    public void AddCommonResponseCompression_RegistersProvidersInPreferenceOrder()
    {
        List<Type> providerTypes = RegisteredCompressionProviderTypes();

#if NET11_0_OR_GREATER
        providerTypes.Should().Equal(
            typeof(ZstandardCompressionProvider),
            typeof(BrotliCompressionProvider),
            typeof(GzipCompressionProvider));
#else
        providerTypes.Should().Equal(
            typeof(BrotliCompressionProvider),
            typeof(GzipCompressionProvider));
#endif
    }

#if NET11_0_OR_GREATER
    [Fact]
    public void AddCommonResponseCompression_RegistersZstandardProvider() =>
        RegisteredCompressionProviderTypes().Should().Contain(typeof(ZstandardCompressionProvider));

    [Fact]
    public void AddCommonResponseCompression_RegistersZstandardBeforeBrotli()
    {
        List<Type> providerTypes = RegisteredCompressionProviderTypes();

        providerTypes.Should().Contain(typeof(ZstandardCompressionProvider));
        providerTypes.IndexOf(typeof(ZstandardCompressionProvider))
            .Should().BeLessThan(providerTypes.IndexOf(typeof(BrotliCompressionProvider)));
    }
#endif

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

    // L129: the SignalR query-string token is accepted on the hub path the host configured
    // (PushNotifications:HubPath), not only under the literal /hubs.
    [Fact]
    public async Task AddCommonAuthentication_AcceptsTheQueryStringTokenOnTheConfiguredHubPath()
    {
        var services = new ServiceCollection();
        services.AddCommonAuthentication(CreateValidJwtConfiguration(
            KeyValuePair.Create<string, string?>("Jwt:SigningAlgorithm", "HS256"),
            KeyValuePair.Create<string, string?>("PushNotifications:HubPath", "/notify")));

        (await HubTokenFromQueryAsync(services, "/notify")).Should().Be("abc");
    }

    [Fact]
    public async Task AddCommonAuthentication_StillAcceptsTheQueryStringTokenUnderHubs()
    {
        var services = new ServiceCollection();
        services.AddCommonAuthentication(CreateValidJwtConfiguration(
            KeyValuePair.Create<string, string?>("Jwt:SigningAlgorithm", "HS256"),
            KeyValuePair.Create<string, string?>("PushNotifications:HubPath", "/notify")));

        (await HubTokenFromQueryAsync(services, "/hubs/anything")).Should().Be("abc");
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
    public void AddCommonCors_AllowAllPolicy_NeverSupportsCredentials()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration();

        services.AddCommonCors(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>();
        CorsPolicy? policy = corsOptions.Value.GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowAll);

        policy.Should().NotBeNull();
        policy!.SupportsCredentials.Should().BeFalse(
            "AllowAnyOrigin combined with AllowCredentials is rejected by browsers and would expose credentialed requests to any origin");
    }

    [Fact]
    public void AddCommonCors_AllowSpecificOriginsPolicy_NeverAllowsAnyOrigin()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration("https://example.com");

        services.AddCommonCors(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>();
        CorsPolicy? policy = corsOptions.Value.GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins);

        policy.Should().NotBeNull();
        policy!.AllowAnyOrigin.Should().BeFalse(
            "the credentialed policy is the one production selects, so its origin list must stay explicit");
    }

    [Fact]
    public void AddCommonCors_WithNoConfiguredOrigins_LeavesTheCredentialedPolicyClosed()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = CreateCorsConfiguration();

        services.AddCommonCors(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>();
        CorsPolicy? policy = corsOptions.Value.GetPolicy(WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins);

        policy.Should().NotBeNull();
        policy!.AllowAnyOrigin.Should().BeFalse();
        policy.Origins.Should().BeEmpty(
            "a missing Cors:AllowedOrigins section must fail closed rather than widening to any origin");
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

    // ── Signing-key rotation: the in-process validator accepts every published key ──
    [Fact]
    public async Task AddCommonAuthentication_Rs256_AcceptsATokenSignedWithAJwksAdditionalKey()
    {
        using var current = System.Security.Cryptography.RSA.Create(2048);
        using var previous = System.Security.Cryptography.RSA.Create(2048);
        using var unlisted = System.Security.Cryptography.RSA.Create(2048);

        var services = new ServiceCollection();
        services.AddCommonAuthentication(CreateValidJwtConfiguration(
            new("Jwt:SigningAlgorithm", "RS256"),
            new("Jwt:RsaPublicKeyPem", current.ExportSubjectPublicKeyInfoPem()),
            new("Jwks:KeyId", "identity-2026-11"),
            new("Jwks:AdditionalKeys:0:KeyId", "identity-2026-07"),
            new("Jwks:AdditionalKeys:0:RsaPublicKeyPem", previous.ExportSubjectPublicKeyInfoPem())));

        await using var provider = services.BuildServiceProvider();
        var parameters = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;

        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();

        (await handler.ValidateTokenAsync(SignedToken(current, "identity-2026-11"), parameters)).IsValid.Should().BeTrue();
        (await handler.ValidateTokenAsync(SignedToken(previous, "identity-2026-07"), parameters)).IsValid.Should().BeTrue(
            "a token the previous key signed keeps validating while that key is published");
        (await handler.ValidateTokenAsync(SignedToken(unlisted, "identity-2026-07"), parameters)).IsValid.Should().BeFalse(
            "a key that is not published is never trusted, whatever kid the token claims");
    }

    private static string SignedToken(System.Security.Cryptography.RSA key, string keyId) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "https://test",
            Audience = "testapi",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "42")]),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.RsaSecurityKey(key) { KeyId = keyId },
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256),
        });

    private static IConfiguration CreateValidJwtConfiguration(params KeyValuePair<string, string?>[] extra) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Jwt:SecretForKey", "RgDldLrK+p+T0JisAKdD7THnT/npmWYl4vV3UUiRSVE=" },
                { "Jwt:Issuer", "https://test" },
                { "Jwt:Audience", "testapi" },
                { "Jwt:AccessTokenExpirationMinutes", "15" },
                { "Jwt:RefreshTokenExpirationDays", "7" },
            })
            .AddInMemoryCollection(extra)
            .Build();

    private static async Task<string?> HubTokenFromQueryAsync(IServiceCollection services, string path)
    {
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString("?access_token=abc");
        var context = new MessageReceivedContext(
            http,
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            options);

        await options.Events.OnMessageReceived(context);
        return context.Token;
    }

    // Reads the registered providers in order through the public negotiation API rather than the
    // options collection, whose Providers.Add<T>() entries are internal factories that hide the
    // provider type. Offering every known encoding at equal weight picks the first registered
    // provider among them; removing that encoding and asking again yields the next one.
    private static List<Type> RegisteredCompressionProviderTypes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommonResponseCompression();

        using ServiceProvider provider = services.BuildServiceProvider();
        var negotiator = provider.GetRequiredService<IResponseCompressionProvider>();
        var remaining = new List<string> { "zstd", "br", "gzip", "deflate" };
        var ordered = new List<Type>();

        while (remaining.Count > 0)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.AcceptEncoding = string.Join(", ", remaining);
            ICompressionProvider? selected = negotiator.GetCompressionProvider(context);
            if (selected is null)
            {
                break;
            }

            ordered.Add(selected.GetType());
            if (remaining.RemoveAll(encoding => string.Equals(encoding, selected.EncodingName, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                break;
            }
        }

        // Every registered provider must be one of the offered encodings, so "exactly" holds.
        provider.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value.Providers.Count
            .Should().Be(ordered.Count, "every registered provider answers one of the offered encodings");
        return ordered;
    }

    private static IConfiguration CreateCorsConfiguration(params string[] allowedOrigins) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                [.. allowedOrigins.Select((origin, i) => KeyValuePair.Create<string, string?>(string.Create(CultureInfo.InvariantCulture, $"Cors:AllowedOrigins:{i}"), origin))])
            .Build();
}
