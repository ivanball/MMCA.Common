using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using MMCA.Common.UI.Notifications;

namespace MMCA.Common.UI.Tests.Notifications;

/// <summary>
/// The navigation menu resolves every item title and group label as a resource key against the
/// item's <c>TitleResource</c> and falls back to the RAW key when the resource does not declare it,
/// so a missing key renders untranslated text with no error. Pins that every key the notification
/// module contributes is declared in the neutral resource and in the Spanish satellite.
/// </summary>
public sealed class NotificationNavResourceTests
{
    public static TheoryData<string> Cultures => [string.Empty, "es"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void EveryNotificationNavTitleAndGroup_IsDeclaredInItsResource(string culture)
    {
        var cultureInfo = CultureInfo.GetCultureInfo(culture);

        foreach (var item in new NotificationUIModule().NavItems)
        {
            // tryParents: false, so a key present only in the neutral file does not satisfy the
            // Spanish check by falling back to English.
            var resources = new ResourceManager(item.TitleResource)
                .GetResourceSet(cultureInfo, createIfNotExists: true, tryParents: false);
            resources.Should().NotBeNull($"the {item.TitleResource.Name} resources exist for '{culture}'");

            string?[] keys = [item.Title, item.Group];
            foreach (var key in keys.OfType<string>())
            {
                resources!.GetString(key).Should().NotBeNullOrWhiteSpace(
                    $"the navigation key '{key}' must be declared for culture '{culture}', or the menu shows the raw key");
            }
        }
    }
}
