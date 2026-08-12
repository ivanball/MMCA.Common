namespace MMCA.Common.UI.Components;

/// <summary>
/// Error-correction strength for <c>QrCodeImage</c>. Higher levels survive more damage or
/// occlusion but pack fewer characters into the same module count, so the code grows denser.
/// Declared as a framework enum rather than exposing QRCoder's own <c>ECCLevel</c>, so the
/// component's public API does not pin consumers to the encoder package.
/// </summary>
public enum QrErrorCorrectionLevel
{
    /// <summary>About 7% recovery. Densest code; use only for short payloads on clean screens.</summary>
    Low = 0,

    /// <summary>About 15% recovery. The default: the usual screen and print trade-off.</summary>
    Medium = 1,

    /// <summary>About 25% recovery. Worth it for printed sheets that may get scuffed.</summary>
    Quartile = 2,

    /// <summary>About 30% recovery. For codes overlaid with a logo or scanned in poor light.</summary>
    High = 3,
}
