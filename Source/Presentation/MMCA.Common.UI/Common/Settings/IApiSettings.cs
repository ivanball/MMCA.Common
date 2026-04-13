namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Read-only view of API configuration consumed by HTTP client setup.
/// </summary>
public interface IApiSettings
{
    /// <summary>Base URL of the WebAPI backend.</summary>
    string? ApiEndpoint { get; }

    /// <summary>
    /// API endpoint served to the WebAssembly client via <c>/client-config</c>.
    /// When set, allows the server to use an internal URL for <see cref="ApiEndpoint"/>
    /// (faster, avoids public DNS) while the browser uses this external URL.
    /// Falls back to <see cref="ApiEndpoint"/> when <see langword="null"/>.
    /// </summary>
    string? WasmApiEndpoint { get; }
}
