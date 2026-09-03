using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MMCA.Common.UI.Components.Forms;

/// <summary>
/// Downloads a file produced by an API endpoint. Browsers get a plain download link to the
/// endpoint (the response's attachment disposition does the rest). Native heads cannot download
/// through the WebView, so they fetch the bytes over the API client, stage a temp file, and open
/// the share sheet, which is where "save to Files", "open in Calendar" and the rest live on both
/// platforms. Callers supply the endpoint, the file name, the MIME type and the labels; the button
/// itself knows nothing about what the payload is.
/// </summary>
public partial class ApiFileDownloadButton
{
    /// <summary>Relative API path of the endpoint producing the file, e.g. <c>Sessions/42/ics</c>.</summary>
    [Parameter]
    [EditorRequired]
    public string RelativeApiPath { get; set; } = string.Empty;

    /// <summary>
    /// File name for the staged temp file on native heads, e.g. <c>session-42.ics</c>.
    /// <para>
    /// Directory segments and rooted paths are stripped to the file name before staging, so a value
    /// built from entity data cannot steer the write out of the temp directory.
    /// </para>
    /// </summary>
    [Parameter]
    [EditorRequired]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Title shown on the native share sheet.</summary>
    [Parameter]
    [EditorRequired]
    public string ShareTitle { get; set; } = string.Empty;

    /// <summary>
    /// MIME type handed to the native share sheet, which uses it to pick the target apps.
    /// Defaults to <c>application/octet-stream</c>; pass the real type (e.g. <c>text/calendar</c>)
    /// whenever a specific app should be offered.
    /// </summary>
    [Parameter]
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Button icon. Defaults to the generic download glyph.</summary>
    [Parameter]
    public string Icon { get; set; } = Icons.Material.Filled.Download;

    /// <summary>Button size. Defaults to <see cref="MudBlazor.Size.Small"/>, matching the other detail-page affordances.</summary>
    [Parameter]
    public Size Size { get; set; } = Size.Small;

    /// <summary>
    /// Accessible name for the icon button (ADR-021: an icon-only control must carry one).
    /// Defaults to the localized "Download" label; pass the caller's own wording
    /// (e.g. "Add to calendar") when the generic one would not say what the file is.
    /// </summary>
    [Parameter]
    public string? AriaLabel { get; set; }

    /// <summary>
    /// Toast shown on a native head when no share surface accepted the file. Defaults to the
    /// localized generic sentence.
    /// </summary>
    [Parameter]
    public string? UnavailableMessage { get; set; }

    /// <summary>
    /// Toast shown when the download or the temp-file staging failed. Defaults to the localized
    /// generic sentence.
    /// </summary>
    [Parameter]
    public string? FailureMessage { get; set; }

    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client used for the native fetch.
    /// Defaults to the framework's <c>APIClient</c> (bearer token + culture header).
    /// </summary>
    [Parameter]
    public string HttpClientName { get; set; } = "APIClient";

    private bool _isExporting;

    private string AccessibleLabel => AriaLabel ?? L["Button.Download.Aria"].Value;

    // Browsers need the EXTERNAL gateway URL: WasmApiEndpoint on the Server head (its
    // ApiEndpoint may be container-internal in prod), ApiEndpoint on the WASM head (already
    // the browser-reachable value fetched from /client-config).
    private string BrowserDownloadUrl
    {
        get
        {
            var baseUrl = ApiOptions.Value.WasmApiEndpoint ?? ApiOptions.Value.ApiEndpoint;
            return string.IsNullOrWhiteSpace(baseUrl)
                ? RelativeApiPath
                : new Uri(new Uri(baseUrl), RelativeApiPath).OriginalString;
        }
    }

    private async Task ShareDownloadedFileAsync()
    {
        if (_isExporting || string.IsNullOrWhiteSpace(RelativeApiPath))
        {
            return;
        }

        _isExporting = true;
        try
        {
            // Sanitized BEFORE the fetch: a name that cannot be staged must not cost a download.
            var safeName = ResolveStagedFileName(FileName);
            if (safeName is null)
            {
                Toast.Warning(FailureMessage ?? L["Snackbar.DownloadFailed"].Value);
                return;
            }

            using var client = HttpClientFactory.CreateClient(HttpClientName);
            var bytes = await client.GetByteArrayAsync(new Uri(RelativeApiPath, UriKind.Relative));

            var filePath = await StageFileAsync(safeName, bytes);

            if (!await Share.ShareFileAsync(ShareTitle, filePath, ContentType))
            {
                Toast.Warning(UnavailableMessage ?? L["Snackbar.ShareUnavailable"].Value);
            }
        }
        catch (HttpRequestException)
        {
            Toast.Warning(FailureMessage ?? L["Snackbar.DownloadFailed"].Value);
        }
        catch (OperationCanceledException)
        {
            // No token is passed to the download, so this is the HttpClient timeout, not a
            // disposal. Report it rather than swallowing it: the user tapped and got nothing.
            Toast.Warning(FailureMessage ?? L["Snackbar.DownloadFailed"].Value);
        }
        catch (Exception)
        {
            // The staging write (IOException, UnauthorizedAccessException) and the share sheet
            // itself run here too. This is an OnClick callback on the native heads, where an
            // unhandled exception is fatal to the host, so nothing may escape.
            Toast.Warning(FailureMessage ?? L["Snackbar.DownloadFailed"].Value);
        }
        finally
        {
            _isExporting = false;
        }
    }

    /// <summary>
    /// Reduces <paramref name="fileName"/> to a bare file name, or returns null when nothing usable
    /// is left.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/> discards the temp root outright when its second
    /// argument is rooted, and <c>..</c> segments walk out of it, so a name built from entity data
    /// would otherwise decide where the staging delete and write land on the native head's
    /// filesystem. <see cref="Path.GetFileName(string)"/> strips separators and directory segments;
    /// the two relative-directory names it leaves intact are rejected here.
    /// </remarks>
    private static string? ResolveStagedFileName(string? fileName)
    {
        var safeName = Path.GetFileName(fileName?.Trim() ?? string.Empty);

        return string.IsNullOrEmpty(safeName)
            || string.Equals(safeName, ".", StringComparison.Ordinal)
            || string.Equals(safeName, "..", StringComparison.Ordinal)
                ? null
                : safeName;
    }

    /// <summary>Writes the payload into the temp directory under an already-sanitized name.</summary>
    /// <remarks>
    /// The staged file is not deleted after the share: on Android the share intent returns as soon
    /// as it launches, so deleting here would race the receiving app reading it. The path is
    /// per-entity, so a stale copy from a previous tap is simply replaced; clearing it first keeps a
    /// truncated or half-written leftover from being shared.
    /// </remarks>
    private static async Task<string> StageFileAsync(string safeName, byte[] bytes)
    {
        var filePath = Path.Combine(Path.GetTempPath(), safeName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        await File.WriteAllBytesAsync(filePath, bytes);

        return filePath;
    }
}
