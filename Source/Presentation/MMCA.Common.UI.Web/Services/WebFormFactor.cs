using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Web.Services;

/// <summary>
/// Blazor Server-side <see cref="IFormFactor"/> implementation. Reports "Web" because this code runs
/// on the server during SSR prerender and interactive Server render mode. Hoisted from the app Blazor
/// Web hosts (it carries no app-specific state); its siblings are <see cref="WasmFormFactor"/> in
/// MMCA.Common.UI and <c>MauiFormFactor</c> in MMCA.Common.UI.Maui. Register via
/// <c>AddCommonWebFormFactor()</c>.
/// </summary>
public sealed class WebFormFactor : IFormFactor
{
    /// <inheritdoc/>
    public string GetFormFactor() => "Web";

    /// <inheritdoc/>
    public string GetPlatform() => Environment.OSVersion.ToString();
}
