namespace MMCA.UI.Shared.Common.Interfaces;

/// <summary>
/// Contract for application-specific home page content. Each consuming application registers
/// its own implementation to provide a custom landing page component rendered via
/// <see cref="Microsoft.AspNetCore.Components.DynamicComponent"/> at the "/" route.
/// </summary>
public interface IHomePageContent
{
    /// <summary>The Razor component type to render as the home page body.</summary>
    Type ComponentType { get; }

    /// <summary>The page title displayed in the browser tab.</summary>
    string PageTitle { get; }
}
