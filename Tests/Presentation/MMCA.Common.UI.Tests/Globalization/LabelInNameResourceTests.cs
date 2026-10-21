using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Tests.Globalization;

/// <summary>
/// Pins WCAG 2.5.3 Label in Name for every shared control that carries BOTH visible text and an
/// aria-label: in every shipped culture the aria-label must contain the visible text, or axe
/// <c>label-content-name-mismatch</c> fails the page and a speech-input user saying what they see
/// cannot activate the control. A translation is where this breaks silently (the Spanish per-device
/// sign-out label once inserted an article into the visible phrase, so it no longer contained it),
/// so each pair is checked per culture against the resources themselves.
/// </summary>
public sealed class LabelInNameResourceTests
{
    private const string Argument = "Admin";

    private static readonly ResourceManager Shared = new(typeof(SharedResource));
    private static readonly ResourceManager RoleList = new(typeof(RoleAdminListResources));
    private static readonly ResourceManager UserList = new(typeof(UserAdminListResources));

    public static TheoryData<string, string, string, string> Pairs()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var culture in new[] { "en", "es" })
        {
            data.Add(culture, nameof(RoleList), "Action.Edit", "Aria.Edit");
            data.Add(culture, nameof(Shared), "Auth.Sessions.Revoke", "Auth.Sessions.Revoke.Aria");
            data.Add(culture, nameof(Shared), "Auth.Login.SignIn", "Auth.Login.SignIn.Aria");
            data.Add(culture, nameof(UserList), "Button.Cancel", "Aria.CancelLoading");
            data.Add(culture, nameof(UserList), "Action.ViewDetails", "Aria.ViewDetails");
            data.Add(culture, nameof(UserList), "Menu.Actions", "Aria.Actions");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void AriaLabel_ContainsTheVisibleText(string culture, string resources, string visibleKey, string ariaKey)
    {
        var manager = resources switch
        {
            nameof(Shared) => Shared,
            nameof(RoleList) => RoleList,
            _ => UserList,
        };
        var info = CultureInfo.GetCultureInfo(culture);

        var visible = manager.GetString(visibleKey, info);
        var aria = manager.GetString(ariaKey, info);
        visible.Should().NotBeNullOrWhiteSpace();
        aria.Should().NotBeNullOrWhiteSpace();

        var name = string.Format(info, aria!, Argument);
        Normalize(name).Should().Contain(
            Normalize(visible!),
            $"the {culture} aria-label '{ariaKey}' must contain the visible text '{visibleKey}' (WCAG 2.5.3)");
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
