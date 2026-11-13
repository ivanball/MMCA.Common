using AwesomeAssertions;
using MMCA.Common.Domain.ProcessManagers;

namespace MMCA.Common.Domain.Tests.ProcessManagers;

/// <summary>
/// Tests for <see cref="ProcessTimeout"/>: a named window measured from the last transition. A blank
/// name or a window that is not strictly positive is a programming error, so the constructor guards it.
/// </summary>
public sealed class ProcessTimeoutTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_Throws(string? name)
    {
        var act = () => new ProcessTimeout(name!, TimeSpan.FromMinutes(5));

        act.Should().Throw<ArgumentException>("a timeout is matched by name, so a blank name can never match");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-TimeSpan.TicksPerMinute)]
    public void ZeroWindow_Throws(long windowTicks)
    {
        var act = () => new ProcessTimeout("PaymentExpiry", TimeSpan.FromTicks(windowTicks));

        act.Should().Throw<ArgumentOutOfRangeException>("a window that is not strictly positive is due before it starts");
    }

    [Fact]
    public void DueAfter_AddsWindow()
    {
        var lastTransitionOn = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var timeout = new ProcessTimeout("PaymentExpiry", TimeSpan.FromMinutes(30));

        timeout.DueAfter(lastTransitionOn).Should().Be(lastTransitionOn.AddMinutes(30));
    }

    [Fact]
    public void Properties_ExposeTheConstructorArguments()
    {
        var timeout = new ProcessTimeout("PaymentExpiry", TimeSpan.FromMinutes(30));

        timeout.Name.Should().Be("PaymentExpiry");
        timeout.Window.Should().Be(TimeSpan.FromMinutes(30));
    }
}
