using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"Api"</c> configuration section.
/// Validated at startup via <c>ValidateDataAnnotations</c> to fail fast when the endpoint is missing.
/// </summary>
public sealed class ApiSettings : IApiSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "Api";

    /// <summary>Base URL of the WebAPI (e.g., <c>https://localhost:6001</c>).</summary>
    [Required]
    public string? ApiEndpoint { get; init; }

    /// <inheritdoc />
    public string? WasmApiEndpoint { get; init; }
}
