namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Read-only view of API configuration consumed by HTTP client setup.
/// </summary>
public interface IApiSettings
{
    /// <summary>Base URL of the WebAPI backend.</summary>
    string? ApiEndpoint { get; }
}
