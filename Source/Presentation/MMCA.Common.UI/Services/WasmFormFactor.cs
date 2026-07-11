namespace MMCA.Common.UI.Services;

/// <summary>
/// WebAssembly <see cref="IFormFactor"/> implementation. Reports "WebAssembly" because this code runs
/// entirely in the browser after the WASM runtime has loaded. Hoisted from the app WASM clients (it
/// carries no app-specific state); its siblings are <c>WebFormFactor</c> in MMCA.Common.UI.Web and
/// <c>MauiFormFactor</c> in MMCA.Common.UI.Maui. Register via <c>AddWasmFormFactor()</c>.
/// </summary>
public sealed class WasmFormFactor : IFormFactor
{
    /// <inheritdoc/>
    public string GetFormFactor() => "WebAssembly";

    /// <inheritdoc/>
    public string GetPlatform() => Environment.OSVersion.ToString();
}
