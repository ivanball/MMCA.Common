using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"Layout"</c> configuration section.
/// Provides application-specific layout customization such as the navbar brand and footer text.
/// </summary>
public sealed class LayoutSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "Layout";

    /// <summary>Brand text shown in the top-left navbar link. Defaults to <c>"MMCA"</c> when not configured.</summary>
    public string BrandName { get; init; } = "MMCA";

    /// <summary>Text displayed in the application footer. Defaults to empty when not configured.</summary>
    public string FooterText { get; init; } = string.Empty;

    /// <summary>
    /// Optional URL of a brand logo rendered beside <see cref="BrandName"/> in the navigation brand
    /// link. Empty (the default) renders the text-only brand exactly as before. The image is
    /// decorative (empty alt text): the brand link already carries its own accessible name, so an
    /// alt string here would only repeat it to a screen reader.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "Bound from configuration and emitted straight into an img src, which is normally a host-relative path (e.g. /img/logo.svg). System.Uri cannot represent that without RelativeOrAbsolute round-tripping, and the other endpoint settings in this namespace are strings for the same reason.")]
    public string BrandLogoUrl { get; init; } = string.Empty;

    /// <summary>
    /// Optional role name that gates the framework-owned "Signed-in devices" entry in the
    /// navigation menu. Null or whitespace (the default) shows the link to every signed-in user;
    /// one of the host's own role constants shows it only to users in that role.
    /// This gates the menu entry alone, not the <c>/profile/sessions</c> page itself, which stays
    /// available to any signed-in account.
    /// </summary>
    public string? SessionsNavRequiredRole { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the document element carries
    /// <c>data-mmca-color-mode="light|dark"</c>, kept in step with the Day/Dark toggle, so an app's own
    /// stylesheet can follow the mode (MudThemeProvider only swaps its palette variables, which gives
    /// CSS nothing to select on). Set once the page is interactive, so a prerendered page has no value
    /// until then; a MAUI head that loads <c>theme-prepaint.js</c> with <c>data-stamp-color-mode</c> on
    /// its script tag gets the value before the first paint instead. <see langword="false"/> (the
    /// default) sets nothing. The attribute is distinct from the MAUI head's pre-paint
    /// <c>data-mmca-theme</c>, whose rules it does not trigger.
    /// </summary>
    public bool StampColorModeOnDocument { get; init; }
}
