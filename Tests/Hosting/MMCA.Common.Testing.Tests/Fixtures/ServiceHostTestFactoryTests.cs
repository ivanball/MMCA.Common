using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;
using MMCA.Common.Testing.Fixtures;
using MMCA.Common.Testing.Support;
using Xunit;

namespace MMCA.Common.Testing.Tests.Fixtures;

/// <summary>
/// Covers <see cref="ServiceHostTestFactory{TProgram}"/>: the base every per-service integration
/// factory derives from so each one takes the same test defaults (the <c>Testing</c> environment,
/// relaxed JWT metadata, in-process token validation when the host validates a forwarded bearer, a
/// neutralized global limiter) through two hooks instead of restating them.
/// <para>
/// Harness: the test defaults are applied to a plain generic web host built here, never to a real
/// entry point. The host's own registrations are added BEFORE the defaults, which is the order a
/// real <c>WebApplicationFactory</c> sees: the program's <c>ConfigureServices</c> runs first and the
/// factory's <c>ConfigureWebHost</c> lands after it. On a generic web host
/// <c>ConfigureTestServices</c> is a plain <c>ConfigureServices</c> in call order, so adding the
/// host's registrations after the defaults would invert that and let the host win.
/// </para>
/// </summary>
public sealed class ServiceHostTestFactoryTests
{
    private const string TestAudience = "MMCA.Tests.api";

    private interface IFoo
    {
        string Name { get; }
    }

    [Fact]
    public void Environment_IsTesting()
    {
        using var factory = new TestFactory();
        using var host = BuildHost(factory);

        host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName
            .Should().Be(ServiceHostTestFactory<FakeEntryPoint>.TestingEnvironmentName);
        ServiceHostTestFactory<FakeEntryPoint>.TestingEnvironmentName.Should().Be("Testing");
    }

    [Fact]
    public void RequireHttpsMetadataKey_IsFalse()
    {
        using var factory = new TestFactory();
        using var host = BuildHost(factory);

        host.Services.GetRequiredService<IConfiguration>()[WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey]
            .Should().Be("false", "an in-process test host serves no HTTPS metadata endpoint");
    }

    [Fact]
    public void WithAudience_RepointsTheBearerScheme()
    {
        using var factory = new TestFactory { AudienceOverride = TestAudience };
        using var host = BuildHost(factory);

        var options = BearerOptions(host);

        options.Authority.Should().BeNull("discovery against the Identity service's JWKS must be switched off");
        options.TokenValidationParameters.ValidAudience.Should().Be(TestAudience);
        options.TokenValidationParameters.ValidIssuer.Should().Be(JwtTokenGenerator.DefaultIssuer);
        options.TokenValidationParameters.IssuerSigningKey.Should().NotBeNull();
        options.TokenValidationParameters.IssuerSigningKey!.KeyId.Should().Be(JwtTokenGenerator.DefaultKeyId);
    }

    [Fact]
    public void WithoutAudience_LeavesTheBearerSchemeAlone()
    {
        using var factory = new TestFactory();
        using var host = BuildHost(factory);

        var options = BearerOptions(host);

        options.TokenValidationParameters.ValidAudience.Should().BeNull("an Identity host validates its own tokens");
        options.TokenValidationParameters.IssuerSigningKey.Should().BeNull();
    }

    [Fact]
    public void DefaultPartition_NeutralizesTheGlobalLimiter()
    {
        using var factory = new TestFactory();
        using var host = BuildHost(factory);

        var limiter = host.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;

        limiter.Should().NotBeNull("the default partition installs the no-limiter");
        var context = new DefaultHttpContext();
        for (var i = 0; i < 1000; i++)
        {
            using var lease = limiter!.AttemptAcquire(context);
            lease.IsAcquired.Should().BeTrue("acquisition {0} must be admitted by the neutralized limiter", i);
        }
    }

    [Fact]
    public void NullPartition_LeavesTheLimiterUntouched()
    {
        using var factory = new TestFactory { ClearPartition = true };
        using var host = BuildHost(factory);

        host.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter
            .Should().BeNull("the host registered no global limiter and a null partition must not add one");
    }

    [Fact]
    public void ConfigureHost_RunsBeforeTestServices()
    {
        using var factory = new TestFactory();
        using var host = BuildHost(factory);

        factory.Calls.Should().Equal(["host", "services"], "ConfigureHost runs once, before the test-services callback");
    }

    [Fact]
    public void ConfigureTestServices_FakeWinsOverTheHostRegistration()
    {
        using var factory = new TestFactory
        {
            TestServices = static services => services.AddSingleton<IFoo, FakeFoo>(),
        };
        using var host = BuildHost(factory, static services => services.AddSingleton<IFoo, RealFoo>());

        host.Services.GetRequiredService<IFoo>().Should().BeOfType<FakeFoo>();
    }

    [Fact]
    public void ConfigureTestServices_RunsAfterTheAudienceRepoint()
    {
        // Rule 5 order: the audience post-configure (a) is registered before the subclass hook (c), so
        // a subclass that adjusts the bearer options in its hook has the last word.
        using var factory = new TestFactory
        {
            AudienceOverride = TestAudience,
            TestServices = static services => services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                static options => options.TokenValidationParameters.ValidAudience = "subclass-audience"),
        };
        using var host = BuildHost(factory);

        BearerOptions(host).TokenValidationParameters.ValidAudience.Should().Be("subclass-audience");
    }

    [Fact]
    public void ConfigureWebHost_NullBuilder_Throws()
    {
        using var factory = new TestFactory();

        var act = () => factory.CallConfigureWebHost(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConfigureWebHost_AppliesTheTestDefaults()
    {
        // Rule 1: ConfigureWebHost is ApplyTestDefaults and nothing else, so the same host built
        // through the protected override carries the same defaults.
        using var factory = new TestFactory { AudienceOverride = TestAudience };
        using var host = BuildHost(factory, throughConfigureWebHost: true);

        host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.Should().Be("Testing");
        host.Services.GetRequiredService<IConfiguration>()[WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey]
            .Should().Be("false");
        BearerOptions(host).TokenValidationParameters.ValidAudience.Should().Be(TestAudience);
        host.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter.Should().NotBeNull();
        factory.Calls.Should().Equal(["host", "services"], "the override applies the same hooks in the same order");
    }

    [Fact]
    public void Defaults_AreNoAudience_AndTheIntegrationTestsPartition()
    {
        using var factory = new DefaultsFactory();

        factory.ExposedAudience.Should().BeNull();
        factory.ExposedPartition.Should().Be("integration-tests");
    }

    [Fact]
    public void CreateHost_IsNotOverridden()
    {
        // Rule 9: a subclass's own CreateHost (the two-host Kestrel override in a cross-service tier)
        // must keep working, so the base leaves it to WebApplicationFactory.
        var createHost = typeof(ServiceHostTestFactory<FakeEntryPoint>).GetMethod(
            "CreateHost",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(IHostBuilder)]);

        createHost.Should().NotBeNull();
        createHost!.DeclaringType.Should().Be<WebApplicationFactory<FakeEntryPoint>>();
    }

    [Fact]
    public void TheEnvironmentVariable_IsUntouched()
    {
        // Rule 10: fixtures keep setting ASPNETCORE_ENVIRONMENT themselves; the base sets the host's
        // environment through the builder only.
        var before = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        using var factory = new TestFactory();
        using var host = BuildHost(factory);

        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT").Should().Be(before);
    }

    private static JwtBearerOptions BearerOptions(IHost host) =>
        host.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

    private static IHost BuildHost(
        TestFactory factory,
        Action<IServiceCollection>? hostServices = null,
        bool throughConfigureWebHost = false) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();

                // DESIGN-QUESTION: design 27(c) adds the host registrations AFTER ApplyTestDefaults; on a
                // generic web host ConfigureTestServices is a plain ConfigureServices in call order, so that
                // order would let the host registration win and fail the fake-wins test. Host first here.
                // The host's own registrations first, as the program's ConfigureServices would be.
                web.ConfigureServices(services =>
                {
                    services.AddAuthentication().AddJwtBearer();
                    hostServices?.Invoke(services);
                });

                if (throughConfigureWebHost)
                {
                    factory.CallConfigureWebHost(web);
                }
                else
                {
                    factory.ApplyTestDefaults(web);
                }

                web.Configure(static _ => { });
            })
            .Build();

    /// <summary>A stand-in entry point; the factory is never asked to boot it.</summary>
    public sealed class FakeEntryPoint;

    private sealed class TestFactory : ServiceHostTestFactory<FakeEntryPoint>
    {
        public string? AudienceOverride { get; init; }

        public bool ClearPartition { get; init; }

        public Action<IServiceCollection>? TestServices { get; init; }

        public List<string> Calls { get; } = [];

        protected override string? Audience => AudienceOverride;

        protected override string? RateLimiterPartitionName => ClearPartition ? null : base.RateLimiterPartitionName;

        public void CallConfigureWebHost(IWebHostBuilder b) => ConfigureWebHost(b);

        protected override void ConfigureHost(IWebHostBuilder builder) => Calls.Add("host");

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            Calls.Add("services");
            TestServices?.Invoke(services);
        }
    }

    private sealed class DefaultsFactory : ServiceHostTestFactory<FakeEntryPoint>
    {
        public string? ExposedAudience => Audience;

        public string? ExposedPartition => RateLimiterPartitionName;
    }

    private sealed class RealFoo : IFoo
    {
        public string Name => nameof(RealFoo);
    }

    private sealed class FakeFoo : IFoo
    {
        public string Name => nameof(FakeFoo);
    }
}
