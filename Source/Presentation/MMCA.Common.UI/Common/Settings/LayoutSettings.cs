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
}
