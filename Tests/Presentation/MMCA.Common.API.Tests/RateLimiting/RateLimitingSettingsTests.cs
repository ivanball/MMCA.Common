using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.API.RateLimiting;

namespace MMCA.Common.API.Tests.RateLimiting;

/// <summary>
/// Guards the defaults and the binding of <see cref="RateLimitingSettings"/>. The defaults are
/// load-bearing: they are what the long-standing permit-count overload of
/// <c>AddCommonRateLimiting</c> delegates to, so drifting one of them silently re-tunes every host
/// that never wrote a <c>RateLimiting</c> section.
/// </summary>
public sealed class RateLimitingSettingsTests
{
    [Fact]
    public void Defaults_MatchThePermitCountOverloadsDefaults()
    {
        var settings = new RateLimitingSettings();

        settings.PermitLimit.Should().Be(100);
        settings.QueueLimit.Should().Be(2);
        settings.PerUserPermitLimit.Should().Be(30);
        settings.GlobalPermitLimit.Should().Be(300);
        settings.AuthIpPermitLimit.Should().Be(30);
        settings.Algorithm.Should().Be(RateLimitAlgorithm.FixedWindow);
        settings.SegmentsPerWindow.Should().Be(4);
        settings.Distributed.Should().BeFalse();
    }

    [Fact]
    public void SectionName_IsRateLimiting() =>
        RateLimitingSettings.SectionName.Should().Be("RateLimiting");

    [Fact]
    public void Bind_FromConfiguration_ReadsEveryProperty()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = "50",
                ["RateLimiting:QueueLimit"] = "7",
                ["RateLimiting:PerUserPermitLimit"] = "11",
                ["RateLimiting:GlobalPermitLimit"] = "600",
                ["RateLimiting:AuthIpPermitLimit"] = "13",
                ["RateLimiting:Algorithm"] = "SlidingWindow",
                ["RateLimiting:SegmentsPerWindow"] = "6",
                ["RateLimiting:Distributed"] = "true",
            })
            .Build();

        var settings = configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>();

        settings.Should().NotBeNull();
        settings.PermitLimit.Should().Be(50);
        settings.QueueLimit.Should().Be(7);
        settings.PerUserPermitLimit.Should().Be(11);
        settings.GlobalPermitLimit.Should().Be(600);
        settings.AuthIpPermitLimit.Should().Be(13);
        settings.Algorithm.Should().Be(RateLimitAlgorithm.SlidingWindow);
        settings.SegmentsPerWindow.Should().Be(6);
        settings.Distributed.Should().BeTrue();
    }

    // A partially specified section must leave the untouched knobs at their defaults, or adding one
    // line of configuration would silently reset the rest of the limiter.
    [Fact]
    public void Bind_FromPartialConfiguration_LeavesOtherPropertiesAtDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:GlobalPermitLimit"] = "900",
            })
            .Build();

        var settings = configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>();

        settings.Should().NotBeNull();
        settings.GlobalPermitLimit.Should().Be(900);
        settings.PermitLimit.Should().Be(100);
        settings.Algorithm.Should().Be(RateLimitAlgorithm.FixedWindow);
        settings.Distributed.Should().BeFalse();
    }

    // An absent section binds to null; AddCommonRateLimiting(IConfiguration) is what turns that
    // into the default settings instance.
    [Fact]
    public void Bind_WhenSectionAbsent_ReturnsNull()
    {
        var configuration = new ConfigurationBuilder().Build();

        configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>().Should().BeNull();
    }
}
