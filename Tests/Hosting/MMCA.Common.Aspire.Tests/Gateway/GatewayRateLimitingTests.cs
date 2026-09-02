using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// Unit tests for the edge rate limiter: the settings defaults and their binding, the bypass
/// matcher (the part a later refactor is most likely to "tidy" into throttled health probes), the
/// fail-open posture on an unattributable client, and the fact that registering twice does not
/// throw.
/// </summary>
public sealed class GatewayRateLimitingTests
{
    /// <summary>A well-formed synthetic-traffic secret: long enough to clear the 32-character floor.</summary>
    private const string ConfiguredSecret = "synthetic-traffic-secret-for-tests-0000000";

    [Fact]
    public void Settings_Defaults_MatchTheDocumentedEdgeBudget()
    {
        var settings = new GatewayRateLimitingSettings();

        settings.PermitLimit.Should().Be(120);
        settings.WindowSeconds.Should().Be(60);
        settings.GlobalConcurrencyLimit.Should().Be(200);
        settings.BypassPathPrefixes.Should().BeEmpty();
        settings.SyntheticTrafficHeaderName.Should().Be("X-Synthetic-Traffic-Key");
        settings.SyntheticTrafficSecret.Should().BeNull(
            because: "the synthetic-traffic bypass is off until a host configures a secret");
        GatewayRateLimitingSettings.SectionName.Should().Be("GatewayRateLimiting");
    }

    [Fact]
    public void Settings_BindFromTheGatewayRateLimitingSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewayRateLimiting:PermitLimit"] = "42",
                ["GatewayRateLimiting:WindowSeconds"] = "10",
                ["GatewayRateLimiting:GlobalConcurrencyLimit"] = "7",
                ["GatewayRateLimiting:BypassPathPrefixes:0"] = "/metrics",
                ["GatewayRateLimiting:BypassPathPrefixes:1"] = "/status",
                ["GatewayRateLimiting:SyntheticTrafficHeaderName"] = "X-Load-Proof",
                ["GatewayRateLimiting:SyntheticTrafficSecret"] = ConfiguredSecret,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(configuration);

        var settings = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GatewayRateLimitingSettings>>().Value;

        settings.PermitLimit.Should().Be(42);
        settings.WindowSeconds.Should().Be(10);
        settings.GlobalConcurrencyLimit.Should().Be(7);
        settings.BypassPathPrefixes.Should().Equal("/metrics", "/status");
        settings.SyntheticTrafficHeaderName.Should().Be("X-Load-Proof");
        settings.SyntheticTrafficSecret.Should().Be(ConfiguredSecret);
    }

    [Fact]
    public void Settings_WithNoSection_FallBackToDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(configuration);

        var settings = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GatewayRateLimitingSettings>>().Value;

        settings.PermitLimit.Should().Be(120);
        settings.BypassPathPrefixes.Should().BeEmpty();
    }

    // Health probes and JWKS discovery run at high frequency by design. Throttling them turns a
    // traffic spike into a failed liveness probe and a container restart, so the exemption is
    // asserted directly rather than left to review.
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/HEALTH")]
    [InlineData("/alive")]
    [InlineData("/.well-known/jwks.json")]
    public void IsBypassed_AlwaysExemptsInfrastructurePaths(string path) =>
        GatewayRateLimitingExtensions.IsBypassed(new PathString(path), new GatewayRateLimitingSettings())
            .Should().BeTrue();

    [Theory]
    [InlineData("/api/v1/orders")]
    [InlineData("/")]
    [InlineData("/healthz")]
    public void IsBypassed_DoesNotExemptOrdinaryTraffic(string path) =>
        GatewayRateLimitingExtensions.IsBypassed(new PathString(path), new GatewayRateLimitingSettings())
            .Should().BeFalse(because: "matching is on whole segments, so /healthz is not /health");

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/METRICS/prometheus")]
    public void IsBypassed_ExemptsConfiguredPrefixesCaseInsensitively(string path) =>
        GatewayRateLimitingExtensions.IsBypassed(
            new PathString(path),
            new GatewayRateLimitingSettings { BypassPathPrefixes = ["/metrics"] })
            .Should().BeTrue();

    [Fact]
    public void IsBypassed_IgnoresBlankConfiguredPrefixes() =>
        GatewayRateLimitingExtensions.IsBypassed(
            new PathString("/api/orders"),
            new GatewayRateLimitingSettings { BypassPathPrefixes = ["  "] })
            .Should().BeFalse(because: "a blank prefix would otherwise disable the limiter entirely");

    [Fact]
    public void ClientIpPartition_ForAnonymousTraffic_StillLimitsByIp()
    {
        var context = ContextFor("/api/orders", IPAddress.Parse("203.0.113.7"));

        var partition = GatewayRateLimitingExtensions.ClientIpPartition(context, new GatewayRateLimitingSettings());

        partition.PartitionKey.Should().Be("203.0.113.7",
            because: "the edge is exactly where an unauthenticated flood has to be stopped");
    }

    [Fact]
    public void ClientIpPartition_WithUnresolvableIp_FailsOpen()
    {
        var context = ContextFor("/api/orders", remoteIp: null);

        var partition = GatewayRateLimitingExtensions.ClientIpPartition(context, new GatewayRateLimitingSettings());

        partition.PartitionKey.Should().Be("__unknown-ip");
    }

    [Fact]
    public void ClientIpPartition_OnABypassedPath_UsesTheBypassPartition()
    {
        var context = ContextFor("/alive", IPAddress.Loopback);

        GatewayRateLimitingExtensions.ClientIpPartition(context, new GatewayRateLimitingSettings())
            .PartitionKey.Should().Be("__bypass");
    }

    [Fact]
    public void ConcurrencyPartition_SharesOneBucketForTheWholeReplica()
    {
        var settings = new GatewayRateLimitingSettings();

        var first = GatewayRateLimitingExtensions.ConcurrencyPartition(ContextFor("/a", IPAddress.Loopback), settings);
        var second = GatewayRateLimitingExtensions.ConcurrencyPartition(ContextFor("/b", IPAddress.Any), settings);

        first.PartitionKey.Should().Be("__gateway");
        second.PartitionKey.Should().Be(first.PartitionKey,
            because: "the concurrency ceiling bounds total in-flight work, not per-caller rate");
    }

    [Fact]
    public void ConcurrencyPartition_OnABypassedPath_UsesTheBypassPartition() =>
        GatewayRateLimitingExtensions.ConcurrencyPartition(
            ContextFor("/health", IPAddress.Loopback), new GatewayRateLimitingSettings())
            .PartitionKey.Should().Be("__bypass");

    [Fact]
    public void AddGatewayRateLimiting_RegistersA429RejectionAndAGlobalLimiter()
    {
        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(new GatewayRateLimitingSettings());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        options.RejectionStatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        options.GlobalLimiter.Should().NotBeNull();
    }

    // Assignment (not AddPolicy) for the global limiter, so a host that calls this twice, or calls
    // both overloads, replaces the limiter instead of failing startup on a duplicate policy name.
    [Fact]
    public void AddGatewayRateLimiting_IsIdempotent()
    {
        var services = new ServiceCollection();

        var act = () =>
        {
            services.AddGatewayRateLimiting(new GatewayRateLimitingSettings());
            services.AddGatewayRateLimiting(new GatewayRateLimitingSettings { PermitLimit = 5 });
            return services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task GlobalLimiter_ExhaustsThePerIpWindowAndThenRejects()
    {
        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(new GatewayRateLimitingSettings { PermitLimit = 2, WindowSeconds = 60 });

        await using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;

        var context = ContextFor("/api/orders", IPAddress.Parse("198.51.100.4"));

        using var first = await limiter.AcquireAsync(context, 1, CancellationToken.None);
        using var second = await limiter.AcquireAsync(context, 1, CancellationToken.None);
        using var third = await limiter.AcquireAsync(context, 1, CancellationToken.None);

        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeTrue();
        third.IsAcquired.Should().BeFalse();
    }

    [Fact]
    public async Task GlobalLimiter_NeverRejectsABypassedPath()
    {
        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(new GatewayRateLimitingSettings { PermitLimit = 1, GlobalConcurrencyLimit = 1 });

        await using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;

        var leases = new List<RateLimitLease>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                leases.Add(await limiter.AcquireAsync(ContextFor("/alive", IPAddress.Loopback), 1, CancellationToken.None));
            }

            leases.Should().OnlyContain(l => l.IsAcquired);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    // A single-runner capacity proof arrives entirely from one IP, which the per-IP window cannot
    // tell from an unauthenticated flood. The bypass is what lets such a run measure the system
    // instead of the limiter, so both halves matter: it must work with the secret, and it must be
    // unclaimable without one.
    [Fact]
    public void IsSyntheticTraffic_WithNoSecretConfigured_IsFalseEvenWhenTheHeaderIsPresent()
    {
        var settings = new GatewayRateLimitingSettings();
        var context = ContextFor("/api/orders", IPAddress.Loopback, settings.SyntheticTrafficHeaderName, "anything");

        GatewayRateLimitingExtensions.IsSyntheticTraffic(context, settings)
            .Should().BeFalse(because: "the bypass is off until a host configures a secret");
    }

    [Fact]
    public void IsSyntheticTraffic_WithTheConfiguredSecret_IsTrue()
    {
        var settings = new GatewayRateLimitingSettings { SyntheticTrafficSecret = ConfiguredSecret };
        var context = ContextFor(
            "/api/orders", IPAddress.Loopback, settings.SyntheticTrafficHeaderName, ConfiguredSecret);

        GatewayRateLimitingExtensions.IsSyntheticTraffic(context, settings).Should().BeTrue();
    }

    [Fact]
    public void IsSyntheticTraffic_HonorsAConfiguredHeaderName()
    {
        var settings = new GatewayRateLimitingSettings
        {
            SyntheticTrafficHeaderName = "X-Load-Proof",
            SyntheticTrafficSecret = ConfiguredSecret,
        };

        GatewayRateLimitingExtensions.IsSyntheticTraffic(
            ContextFor("/api/orders", IPAddress.Loopback, "X-Load-Proof", ConfiguredSecret), settings)
            .Should().BeTrue();

        GatewayRateLimitingExtensions.IsSyntheticTraffic(
            ContextFor("/api/orders", IPAddress.Loopback, "X-Synthetic-Traffic-Key", ConfiguredSecret), settings)
            .Should().BeFalse(because: "only the configured header name is read");
    }

    [Theory]
    [InlineData("wrong-value")]
    [InlineData("")]
    [InlineData("synthetic-traffic-secret-for-tests-000000")] // one character short
    [InlineData("synthetic-traffic-secret-for-tests-00000000")] // one character long
    public void IsSyntheticTraffic_WithAWrongValue_IsFalse(string presented)
    {
        var settings = new GatewayRateLimitingSettings { SyntheticTrafficSecret = ConfiguredSecret };

        GatewayRateLimitingExtensions.IsSyntheticTraffic(
            ContextFor("/api/orders", IPAddress.Loopback, settings.SyntheticTrafficHeaderName, presented), settings)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSyntheticTraffic_WithNoHeader_IsFalse()
    {
        var settings = new GatewayRateLimitingSettings { SyntheticTrafficSecret = ConfiguredSecret };

        GatewayRateLimitingExtensions.IsSyntheticTraffic(ContextFor("/api/orders", IPAddress.Loopback), settings)
            .Should().BeFalse();
    }

    // A multi-valued header would let a caller spray candidate secrets in one request, so exactly
    // one value is required rather than "any value matches".
    [Fact]
    public void IsSyntheticTraffic_WithMultipleHeaderValues_IsFalse()
    {
        var settings = new GatewayRateLimitingSettings { SyntheticTrafficSecret = ConfiguredSecret };
        var context = ContextFor(
            "/api/orders", IPAddress.Loopback, settings.SyntheticTrafficHeaderName, "decoy", ConfiguredSecret);

        GatewayRateLimitingExtensions.IsSyntheticTraffic(context, settings)
            .Should().BeFalse(because: "one request may present exactly one candidate");
    }

    [Fact]
    public void ClientIpPartition_ForProvenSyntheticTraffic_UsesTheBypassPartition()
    {
        var settings = new GatewayRateLimitingSettings { SyntheticTrafficSecret = ConfiguredSecret };
        var proven = ContextFor(
            "/api/orders", IPAddress.Parse("203.0.113.7"), settings.SyntheticTrafficHeaderName, ConfiguredSecret);
        var ordinary = ContextFor("/api/orders", IPAddress.Parse("203.0.113.7"));

        GatewayRateLimitingExtensions.ClientIpPartition(proven, settings).PartitionKey.Should().Be("__bypass");
        GatewayRateLimitingExtensions.ClientIpPartition(ordinary, settings).PartitionKey.Should().Be("203.0.113.7");
    }

    [Fact]
    public void ConcurrencyPartition_ForProvenSyntheticTraffic_UsesTheBypassPartition()
    {
        var settings = new GatewayRateLimitingSettings { SyntheticTrafficSecret = ConfiguredSecret };
        var proven = ContextFor(
            "/api/orders", IPAddress.Parse("203.0.113.7"), settings.SyntheticTrafficHeaderName, ConfiguredSecret);
        var ordinary = ContextFor("/api/orders", IPAddress.Parse("203.0.113.7"));

        GatewayRateLimitingExtensions.ConcurrencyPartition(proven, settings).PartitionKey.Should().Be("__bypass");
        GatewayRateLimitingExtensions.ConcurrencyPartition(ordinary, settings).PartitionKey.Should().Be("__gateway");
    }

    // A guessable bypass key is worse than no bypass at all, so a short secret must fail at
    // registration (ADR-070) rather than at the first synthetic run.
    [Fact]
    public void AddGatewayRateLimiting_WithATooShortSecret_ThrowsAtRegistration()
    {
        var act = () => new ServiceCollection().AddGatewayRateLimiting(
            new GatewayRateLimitingSettings { SyntheticTrafficSecret = new string('s', 31) });

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void AddGatewayRateLimiting_WithAThirtyTwoCharacterSecret_IsAccepted()
    {
        var act = () => new ServiceCollection().AddGatewayRateLimiting(
            new GatewayRateLimitingSettings { SyntheticTrafficSecret = new string('s', 32) });

        act.Should().NotThrow();
    }

    [Fact]
    public async Task GlobalLimiter_AdmitsProvenSyntheticTrafficPastAnExhaustedPerIpWindow()
    {
        var settings = new GatewayRateLimitingSettings
        {
            PermitLimit = 1,
            WindowSeconds = 60,
            SyntheticTrafficSecret = ConfiguredSecret,
        };

        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(settings);

        await using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;

        var runnerIp = IPAddress.Parse("198.51.100.9");

        using var first = await limiter.AcquireAsync(ContextFor("/api/orders", runnerIp), 1, CancellationToken.None);
        using var second = await limiter.AcquireAsync(ContextFor("/api/orders", runnerIp), 1, CancellationToken.None);
        using var synthetic = await limiter.AcquireAsync(
            ContextFor("/api/orders", runnerIp, settings.SyntheticTrafficHeaderName, ConfiguredSecret),
            1,
            CancellationToken.None);

        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeFalse(because: "the whole run arrives from one runner IP");
        synthetic.IsAcquired.Should().BeTrue(because: "the proof measures the system, not the limiter");
    }

    private static DefaultHttpContext ContextFor(string path, IPAddress? remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }

    private static DefaultHttpContext ContextFor(
        string path,
        IPAddress? remoteIp,
        string headerName,
        params string[] headerValues)
    {
        var context = ContextFor(path, remoteIp);
        context.Request.Headers[headerName] = new StringValues(headerValues);
        return context;
    }
}
