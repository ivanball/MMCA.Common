namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Opens the platform share affordance (native share sheet on MAUI, <c>navigator.share</c>
/// in browsers). Both methods return <see langword="false"/> when sharing is unavailable so
/// callers can fall back to <see cref="IClipboardService"/> copy-link.
/// </summary>
public interface IShareService
{
    /// <summary>Shares a link with an accompanying title. Returns whether a share UI was presented.</summary>
    Task<bool> ShareLinkAsync(string title, Uri uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shares a local file (e.g. a screenshot). Returns whether a share UI was presented;
    /// browser implementations report <see langword="false"/> (no local file access).
    /// </summary>
    Task<bool> ShareFileAsync(string title, string filePath, string contentType, CancellationToken cancellationToken = default);
}
