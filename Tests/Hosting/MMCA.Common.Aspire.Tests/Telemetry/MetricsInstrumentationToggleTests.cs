using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The metrics cost knobs (rubric §31): a deployed host drops the two highest-volume, lowest-value
/// AppMetrics families by setting <c>Telemetry:DisableHttpClientMetrics=true</c> and/or
/// <c>Telemetry:DisableRuntimeMetrics=true</c>. Anything other than a parseable boolean <see langword="true"/>
/// keeps the instrumentation, so a typo can never silently blind a whole metric family.
/// </summary>
public sealed class MetricsInstrumentationToggleTests
{
    private const string Key = "Telemetry:DisableRuntimeMetrics";

    private static IConfiguration Config(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null)
        {
            values[Key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Absent_KeepsInstrumentation()
        => Extensions.IsInstrumentationDisabled(Config(null), Key)
            .Should().BeFalse("an unset knob must keep the instrumentation (the default)");

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void True_DropsInstrumentation(string raw)
        => Extensions.IsInstrumentationDisabled(Config(raw), Key).Should().BeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("0")] // not a bool literal — must not disable
    [InlineData("yes")] // unparseable
    [InlineData("")] // blank
    public void FalseOrUnparseable_KeepsInstrumentation(string raw)
        => Extensions.IsInstrumentationDisabled(Config(raw), Key).Should().BeFalse();
}
