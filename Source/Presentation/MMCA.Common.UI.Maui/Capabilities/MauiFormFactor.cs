using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IFormFactor"/> implementation. Uses <see cref="DeviceInfo"/> to report the actual
/// device idiom (Phone, Tablet, Desktop) and platform (Android, iOS, Windows, macOS). Hoisted from the
/// app MAUI heads (it carries no app-specific state); its siblings are <c>WebFormFactor</c> in
/// MMCA.Common.UI.Web and <c>WasmFormFactor</c> in MMCA.Common.UI. Register via
/// <c>AddMauiFormFactor()</c>.
/// </summary>
public sealed class MauiFormFactor : IFormFactor
{
    /// <inheritdoc/>
    public string GetFormFactor() => DeviceInfo.Idiom.ToString();

    /// <inheritdoc/>
    public string GetPlatform() => DeviceInfo.Platform.ToString() + " - " + DeviceInfo.VersionString;
}
