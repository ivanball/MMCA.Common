using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Startup;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Testing.Fixtures;

/// <summary>
/// The base for a per-service integration test factory, so every factory takes the same test defaults
/// through two hooks instead of restating them: the <c>Testing</c> environment, relaxed JWT metadata,
/// in-process token validation when the host validates a forwarded bearer, and a neutralized global
/// rate limiter.
/// <para>
/// The defaults are applied in a fixed order: the environment, the
/// <see cref="WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey"/> setting,
/// <see cref="ConfigureHost"/>, then one test-services callback that re-points the bearer scheme
/// (when <see cref="Audience"/> is set), neutralizes the global limiter (when
/// <see cref="RateLimiterPartitionName"/> is set) and finally calls
/// <see cref="ConfigureTestServices"/>. That callback runs after the host's own service registrations,
/// so a fake registered there wins.
/// </para>
/// <para>
/// The base never overrides <c>CreateHost</c>, so a subclass that boots its own Kestrel host keeps
/// doing so, and it touches no environment variable.
/// </para>
/// </summary>
/// <typeparam name="TProgram">The host's entry-point class, typically its <c>Program</c>.</typeparam>
public abstract class ServiceHostTestFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    /// <summary>The environment name every host built by this factory runs under.</summary>
    public const string TestingEnvironmentName = "Testing";

    /// <summary>
    /// The audience the host validates. When set, the host's <c>Bearer</c> scheme is re-pointed at the
    /// committed test key through <see cref="JwtTokenGenerator.ConfigureInProcessTokenValidation"/>.
    /// Null (the default) leaves the scheme alone, which suits an Identity host that validates its own
    /// tokens.
    /// </summary>
    protected virtual string? Audience => null;

    /// <summary>
    /// The partition name the global rate limiter is neutralized under. Null leaves the host's global
    /// limiter untouched. Defaults to <c>integration-tests</c>.
    /// </summary>
    protected virtual string? RateLimiterPartitionName => "integration-tests";

    /// <inheritdoc />
    protected sealed override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ApplyTestDefaults(builder);
    }

    /// <summary>
    /// Adjusts the web host builder (logging, settings) after the test defaults and before any
    /// test-services registration. Runs exactly once.
    /// </summary>
    /// <param name="builder">The web host builder.</param>
    protected virtual void ConfigureHost(IWebHostBuilder builder)
    {
    }

    /// <summary>
    /// Registers fakes and test overrides. Runs after the host's own registrations, after the bearer
    /// re-point and after the limiter neutralization.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    /// <summary>The whole body of <see cref="ConfigureWebHost"/>; a test hook.</summary>
    /// <param name="builder">The web host builder.</param>
    internal void ApplyTestDefaults(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestingEnvironmentName);
        builder.UseSetting(WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey, "false");

        ConfigureHost(builder);

        var audience = Audience;
        var partitionName = RateLimiterPartitionName;
        builder.ConfigureTestServices(services =>
        {
            if (audience is not null)
            {
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => JwtTokenGenerator.ConfigureInProcessTokenValidation(options, audience));
            }

            if (partitionName is not null)
            {
                services.NeutralizeGlobalRateLimiter(partitionName);
            }

            ConfigureTestServices(services);
        });
    }
}
