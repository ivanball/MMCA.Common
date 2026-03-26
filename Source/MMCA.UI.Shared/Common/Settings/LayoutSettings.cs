namespace MMCA.UI.Shared.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"Layout"</c> configuration section.
/// Provides application-specific layout customization such as footer text.
/// </summary>
public sealed class LayoutSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "Layout";

    /// <summary>Text displayed in the application footer. Defaults to empty when not configured.</summary>
    public string FooterText { get; init; } = string.Empty;
}
