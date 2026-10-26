using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>Serializes the tests that set process-wide E2E environment variables.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2EEnvironmentCollection
{
    /// <summary>The collection name the environment-variable tests share.</summary>
    public const string Name = "E2E environment";
}

/// <summary>
/// L241: the E2E millisecond settings parse with the invariant culture and refuse zero, negative and
/// non-finite timeouts, falling back to the default (a 0 is "no timeout" to Playwright). Each fact
/// sets a process-wide environment variable and restores it, so the class runs alone.
/// </summary>
[Collection(E2EEnvironmentCollection.Name)]
public sealed class E2ETestConfigurationTests
{
    /// <summary>A group separator reads the same under a comma-decimal culture.</summary>
    [Fact]
    public void DefaultTimeout_ReadsAThousandsSeparatorTheSameUnderEveryCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            WithVariable("E2E_TIMEOUT", "30,000", () => E2ETestConfiguration.DefaultTimeout).Should().Be(30_000f);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>A zero, negative or non-finite timeout falls back to the default.</summary>
    /// <param name="raw">The raw environment value.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("NaN")]
    public void DefaultTimeout_RefusesAZeroNegativeOrNonFiniteValue(string raw) =>
        WithVariable("E2E_TIMEOUT", raw, () => E2ETestConfiguration.DefaultTimeout).Should().Be(30_000f);

    /// <summary>Zero stays a legal slow-mo value.</summary>
    [Fact]
    public void SlowMo_AcceptsZero() =>
        WithVariable("E2E_SLOWMO", "0", () => E2ETestConfiguration.SlowMo).Should().Be(0f);

    /// <summary>An unset auth timeout inherits the general timeout.</summary>
    [Fact]
    public void AuthTimeout_Unset_FollowsTheDefaultTimeout() =>
        WithVariable("E2E_AUTH_TIMEOUT", null, () => E2ETestConfiguration.AuthTimeout)
            .Should().Be(E2ETestConfiguration.DefaultTimeout);

    private static float WithVariable(string name, string? value, Func<float> read)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            return read();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
