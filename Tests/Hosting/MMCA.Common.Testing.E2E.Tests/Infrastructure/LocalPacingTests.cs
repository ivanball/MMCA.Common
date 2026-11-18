using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="LocalPacing"/>: only a positive whole number of seconds pauses, so CI, which leaves
/// <c>E2E_LOCAL_PACING_SECONDS</c> unset, never waits.
/// </summary>
[Collection(E2EEnvironmentCollection.Name)]
public sealed class LocalPacingTests
{
    /// <summary>The pause is zero unless a positive whole number of seconds is set.</summary>
    /// <param name="raw">The raw environment value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("soon")]
    public void Pause_IsZero_UnlessAPositiveWholeNumberIsSet(string? raw)
    {
        using var scope = new EnvironmentScope(LocalPacing.SecondsVariable, raw);

        LocalPacing.Pause.Should().Be(TimeSpan.Zero);
        LocalPacing.CoolDownAsync().IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>A positive whole number is read as seconds.</summary>
    [Fact]
    public void Pause_ReadsWholeSeconds()
    {
        using var scope = new EnvironmentScope(LocalPacing.SecondsVariable, "20");

        LocalPacing.Pause.Should().Be(TimeSpan.FromSeconds(20));
    }
}
