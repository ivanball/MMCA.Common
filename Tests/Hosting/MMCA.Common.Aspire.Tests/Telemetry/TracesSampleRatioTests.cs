using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Aspire;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The <c>Telemetry:TracesSampleRatio</c> cost knob (rubric §31): a host opts into head-based trace
/// sampling by setting a ratio in (0,1); anything else falls back to sample-everything so a typo can
/// never silently drop all telemetry.
/// </summary>
public sealed class TracesSampleRatioTests
{
    private static IConfiguration Config(string? ratio)
    {
        var values = new Dictionary<string, string?>();
        if (ratio is not null)
        {
            values["Telemetry:TracesSampleRatio"] = ratio;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Absent_FallsBackToSampleEverything()
    {
        var opted = Extensions.TryGetTraceSampleRatio(Config(null), out var ratio);

        opted.Should().BeFalse("an unset knob must sample everything (the default)");
        ratio.Should().Be(1.0);
    }

    [Theory]
    [InlineData("0.1", 0.1)]
    [InlineData("0.5", 0.5)]
    [InlineData("0.001", 0.001)]
    public void ValidRatioInOpenInterval_OptsIn(string raw, double expected)
    {
        var opted = Extensions.TryGetTraceSampleRatio(Config(raw), out var ratio);

        opted.Should().BeTrue();
        ratio.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")] // would drop all traces — refuse, sample everything instead
    [InlineData("1")] // sample-everything is already the default
    [InlineData("1.5")] // out of range
    [InlineData("-0.2")] // negative
    [InlineData("abc")] // unparseable
    [InlineData("")] // blank
    public void OutOfRangeOrUnparseable_FallsBackToSampleEverything(string raw)
    {
        var opted = Extensions.TryGetTraceSampleRatio(Config(raw), out var ratio);

        opted.Should().BeFalse();
        ratio.Should().Be(1.0);
    }
}
