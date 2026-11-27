using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Services.Capabilities.Media;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Media;

/// <summary>
/// <see cref="RepeatScanFilter"/>, the decision the continuous scan session (MAUI and the default
/// interface implementation alike) makes on every decode: O-24 (ADC local test run 21) resubmitted a
/// check-in code about once a second for as long as it stayed in the camera frame.
/// </summary>
public sealed class RepeatScanFilterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheFirstSighting_IsHandled()
    {
        var filter = new RepeatScanFilter(_time);

        filter.IsRepeat("ticket-1").Should().BeFalse();
    }

    [Fact]
    public void TheSameCodeWithinTheWindow_IsARepeat()
    {
        var filter = new RepeatScanFilter(_time);
        filter.IsRepeat("ticket-1");

        _time.Advance(RepeatScanFilter.RepeatWindow - TimeSpan.FromMilliseconds(1));

        filter.IsRepeat("ticket-1").Should().BeTrue();
    }

    [Fact]
    public void TheSameCodeOnceTheWindowHasPassed_IsHandledAgain()
    {
        var filter = new RepeatScanFilter(_time);
        filter.IsRepeat("ticket-1");

        _time.Advance(RepeatScanFilter.RepeatWindow);

        filter.IsRepeat("ticket-1").Should().BeFalse("a deliberate second scan of the same code goes through");
    }

    // A code held in view is decoded again after every dwell; each sighting restarts the window, so it
    // is handled once however long it stays up.
    [Fact]
    public void ACodeHeldInView_StaysARepeat_EvenPastTheWindowFromItsFirstSighting()
    {
        var filter = new RepeatScanFilter(_time);
        filter.IsRepeat("ticket-1");

        for (var sighting = 0; sighting < 5; sighting++)
        {
            _time.Advance(TimeSpan.FromSeconds(1.5));
            filter.IsRepeat("ticket-1").Should().BeTrue();
        }
    }

    [Fact]
    public void ADifferentCode_IsHandled_AndOnlyTheLastCodeIsRemembered()
    {
        var filter = new RepeatScanFilter(_time);
        filter.IsRepeat("ticket-1");

        filter.IsRepeat("ticket-2").Should().BeFalse();
        filter.IsRepeat("ticket-1").Should().BeFalse("only a repeat of the code just handled is skipped");
    }

    [Fact]
    public void CodesDifferingOnlyInCase_AreDifferentCodes()
    {
        var filter = new RepeatScanFilter(_time);
        filter.IsRepeat("Ticket-1");

        filter.IsRepeat("ticket-1").Should().BeFalse("a payload is compared ordinally, as the server compares it");
    }
}
