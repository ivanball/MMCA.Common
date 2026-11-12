using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Gateway.Tests;

/// <summary>
/// L192: the <c>[Required]</c> attributes on the nested trace-header and health-check settings are
/// enforced, through both <c>AddMmcaGateway</c> overloads. A blank trace-header name used to pass
/// startup and then throw <see cref="ArgumentException"/> from <c>Headers.Remove("")</c> on every
/// proxied request; it now fails at registration, and the configuration overload's options
/// pipeline (validated on start) rejects it too.
/// </summary>
public sealed class GatewaySettingsValidationTests
{
    [Theory]
    [InlineData("route", "RouteHeaderName")]
    [InlineData("cluster", "ClusterHeaderName")]
    [InlineData("passive-policy", "Policy")]
    [InlineData("active-policy", "Policy")]
    [InlineData("active-path", "Path")]
    public void AddMmcaGateway_FromSettings_RejectsABlankNestedRequiredValue(string blank, string member)
    {
        var settings = blank switch
        {
            "route" => new GatewaySettings { TraceHeaders = new GatewayTraceHeaderSettings { RouteHeaderName = string.Empty } },
            "cluster" => new GatewaySettings { TraceHeaders = new GatewayTraceHeaderSettings { ClusterHeaderName = "   " } },
            "passive-policy" => new GatewaySettings { HealthCheckDefaults = new GatewayHealthCheckDefaults { Passive = new GatewayPassiveHealthCheckDefaults { Policy = string.Empty } } },
            "active-policy" => new GatewaySettings { HealthCheckDefaults = new GatewayHealthCheckDefaults { Active = new GatewayActiveHealthCheckDefaults { Policy = string.Empty } } },
            _ => new GatewaySettings { HealthCheckDefaults = new GatewayHealthCheckDefaults { Active = new GatewayActiveHealthCheckDefaults { Path = string.Empty } } },
        };
        var services = new ServiceCollection();

        var act = () => services.AddReverseProxy().AddMmcaGateway(settings);

        act.Should().Throw<ValidationException>().Which.Message.Should().Contain(member);
    }

    [Theory]
    [InlineData("TraceHeaders:RouteHeaderName", "RouteHeaderName")]
    [InlineData("TraceHeaders:ClusterHeaderName", "ClusterHeaderName")]
    [InlineData("HealthCheckDefaults:Passive:Policy", "Policy")]
    [InlineData("HealthCheckDefaults:Active:Path", "Path")]
    public void AddMmcaGateway_FromConfiguration_RejectsABlankNestedRequiredValue(string key, string member)
    {
        var configuration = Configuration(("MmcaGateway:" + key, string.Empty));
        var services = new ServiceCollection();

        var act = () => services.AddReverseProxy().AddMmcaGateway(configuration);

        act.Should().Throw<ValidationException>().Which.Message.Should().Contain(member);
    }

    [Fact]
    public void AddMmcaGateway_FromConfiguration_ValidatesTheNestedValuesTheOptionsPipelineBinds()
    {
        // Valid when AddMmcaGateway runs, blanked before the host resolves the options (a test host or
        // a late configuration source), so only the options validator registered for start can catch it.
        var configuration = Configuration(("MmcaGateway:TraceHeaders:RouteHeaderName", "X-Edge-Route"));
        var services = new ServiceCollection();
        services.AddReverseProxy().AddMmcaGateway(configuration);
        configuration["MmcaGateway:TraceHeaders:RouteHeaderName"] = string.Empty;

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<GatewaySettings>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("RouteHeaderName");
    }

    [Fact]
    public void AddMmcaGateway_WithTheDefaults_Validates()
    {
        var services = new ServiceCollection();
        services.AddReverseProxy().AddMmcaGateway(Configuration());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<GatewaySettings>>().Value.TraceHeaders.RouteHeaderName
            .Should().Be(GatewayTraceHeaderSettings.DefaultRouteHeaderName);
    }

    // Item 31, rule 12: the retry block is one of the nested objects, so its ranges fail registration.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RetryAttemptsOutOfRange_FailsRegistration(int attempts)
    {
        var settings = new GatewaySettings
        {
            Retry = new GatewayRetrySettings { Enabled = true, MaxRetryAttempts = attempts },
        };
        var services = new ServiceCollection();

        var act = () => services.AddReverseProxy().AddMmcaGateway(settings);

        act.Should().Throw<ValidationException>().Which.Message.Should().Contain("MaxRetryAttempts");
    }

    [Fact]
    public void RetryDelayOutOfRange_FailsRegistration()
    {
        var settings = new GatewaySettings
        {
            Retry = new GatewayRetrySettings { Enabled = true, Delay = TimeSpan.FromSeconds(6) },
        };

        var fromSettings = () => new ServiceCollection().AddReverseProxy().AddMmcaGateway(settings);
        var fromConfiguration = () => new ServiceCollection().AddReverseProxy().AddMmcaGateway(Configuration(
            ("MmcaGateway:Retry:Enabled", "true"),
            ("MmcaGateway:Retry:Delay", "00:00:06")));

        fromSettings.Should().Throw<ValidationException>().Which.Message.Should().Contain("Delay");
        fromConfiguration.Should().Throw<ValidationException>().Which.Message.Should().Contain("Delay");
    }

    [Fact]
    public void RetryDefaults_AreOffOneAttempt200ms()
    {
        var defaults = new GatewaySettings().Retry;

        defaults.Enabled.Should().BeFalse();
        defaults.MaxRetryAttempts.Should().Be(1);
        defaults.Delay.Should().Be(TimeSpan.FromMilliseconds(200));

        var services = new ServiceCollection();
        services.AddReverseProxy().AddMmcaGateway(Configuration());
        using var provider = services.BuildServiceProvider();
        var bound = provider.GetRequiredService<IOptions<GatewaySettings>>().Value.Retry;

        bound.Enabled.Should().BeFalse();
        bound.MaxRetryAttempts.Should().Be(1);
        bound.Delay.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    private static IConfigurationRoot Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
