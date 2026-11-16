using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Notifications;

/// <summary>
/// ADC manual test Run 6, A-21: Android session reminders were silently dropped when the inexact
/// alarm fired late, because Plugin.LocalNotification discards an alarm more than
/// <c>AndroidScheduleOptions.AllowedDelay</c> past its notify time and that delay defaults to one
/// minute. <c>MauiLocalNotificationService</c> (MMCA.Common.UI.Maui, a MAUI TFM this test project
/// cannot reference, and it calls the MAUI-only plugin, so it cannot be compiled here either) now
/// widens the window. Delivery under Doze is a device check; these tests pin the contract in the source.
/// </summary>
public sealed partial class MauiLocalNotificationServiceLateDeliveryTests
{
    private const int MinimumLateDeliveryMinutes = 5;

    [Fact]
    public void LateDeliveryWindow_IsWellAboveThePluginsOneMinuteDefault()
    {
        var service = ReadEmbedded("MauiLocalNotificationService.cs");

        var window = LateDeliveryWindowDeclaration.Match(service);

        window.Success.Should().BeTrue(
            "the service declares its late-delivery window as `LateDeliveryWindow = TimeSpan.FromMinutes(N)`");
        int.Parse(window.Groups["minutes"].Value, CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(
            MinimumLateDeliveryMinutes,
            "A-21: an inexact alarm can fire minutes late under Doze, and a window near the plugin's 1-minute default drops the reminder");
    }

    [Fact]
    public void LateDeliveryWindow_IsAssignedToTheAndroidAllowedDelay()
    {
        var service = ReadEmbedded("MauiLocalNotificationService.cs");

        AndroidAllowedDelayAssignment.IsMatch(service).Should().BeTrue(
            "A-21: the window only takes effect when the Android schedule options set `AllowedDelay = LateDeliveryWindow`; "
            + "without it the plugin falls back to its 1-minute default");
    }

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = typeof(MauiLocalNotificationServiceLateDeliveryTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(logicalName + " must be embedded as a resource (see the csproj)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\bLateDeliveryWindow\s*=\s*TimeSpan\.FromMinutes\(\s*(?<minutes>\d+)\s*\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LateDeliveryWindowDeclaration { get; }

    // The assignment must sit inside the AndroidScheduleOptions initializer (no closing brace between).
    [GeneratedRegex(@"new\s+AndroidScheduleOptions\s*\{[^}]*\bAllowedDelay\s*=\s*LateDeliveryWindow\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AndroidAllowedDelayAssignment { get; }
}
