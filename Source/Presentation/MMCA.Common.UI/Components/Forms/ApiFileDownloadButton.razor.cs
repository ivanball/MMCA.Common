using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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
    /// <summary>The browser save helper shipped with this package (the authenticated download path).</summary>
    private const string SaveFileModulePath = "./_content/MMCA.Common.UI/file-download.js";

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

    /// <summary>
    /// Whether the endpoint needs the caller's bearer token. Defaults to <see langword="false"/>: the
    /// browser gets the plain download link, which streams a large file without loading it into
    /// memory but carries cookies only, never an Authorization header. When <see langword="true"/>
    /// the browser branch renders no link; a click fetches through the <see cref="HttpClientName"/>
    /// client (the one the native branch uses, which attaches the token on every head, a Blazor
    /// Server circuit included) and saves the bytes through the shipped <c>file-download.js</c>
    /// module. The native branch is the same either way.
    /// </summary>
    [Parameter]
    public bool RequiresAuthentication { get; set; }

    /// <summary>
    /// Optional temp sub-folder the native branch stages the file in, created on demand and purged of
    /// every file before each write, so a sensitive export (a personal-data download, for example)
    /// never outlives the next tap and a head can purge it wholesale at app start. The staged copy
    /// still cannot be deleted after the share: Android's share request returns when the intent
    /// launches. <see langword="null"/> (the default) stages straight in the temp directory, as
    /// before. Reduced to a single folder name like <see cref="FileName"/>; a value with nothing usable
    /// left is a failed download.
    /// </summary>
    [Parameter]
    public string? StagingFolderName { get; set; }

    private bool _isExporting;

    private string AccessibleLabel => AriaLabel ?? L["Button.Download.Aria"].Value;

    // The anchor is a plain top-level navigation, so it carries cookies but never an Authorization
    // header. On a WebAssembly client of a host running the same-origin API proxy the link targets
    // the proxy (the same base the "APIClient" uses): the browser sends the HttpOnly session cookie
    // and the proxy attaches the real bearer server-side, so an authenticated download works without
    // a token in script. Everywhere else browsers need the EXTERNAL gateway URL: WasmApiEndpoint on
    // the Server head (its ApiEndpoint may be container-internal in prod), ApiEndpoint on the WASM
    // head (already the browser-reachable value fetched from /client-config).
    private string BrowserDownloadUrl
    {
        get
        {
            var settings = ApiOptions.Value;
            var baseUrl = settings.SameOriginApiEndpoint ?? settings.WasmApiEndpoint ?? settings.ApiEndpoint;
            return string.IsNullOrWhiteSpace(baseUrl)
                ? RelativeApiPath
                : new Uri(new Uri(baseUrl), RelativeApiPath).OriginalString;
        }
    }

    // The click path of every head that does not use the plain link: a native head (stage the file
    // and open the share sheet) and a browser with RequiresAuthentication (save through JS). Both
    // fetch through the authenticated API client.
    private async Task DownloadThroughClientAsync()
    {
        if (_isExporting || string.IsNullOrWhiteSpace(RelativeApiPath))
        {
            return;
        }

        _isExporting = true;
        try
        {
            // Sanitized BEFORE the fetch: a name or folder that cannot be staged must not cost a download.
            if (!TryResolveTarget(out var safeName, out var safeFolder))
            {
                WarnDownloadFailed();
                return;
            }

            using var client = HttpClientFactory.CreateClient(HttpClientName);
            var bytes = await client.GetByteArrayAsync(new Uri(RelativeApiPath, UriKind.Relative));

            if (ExternalLink.InterceptsLinks)
            {
                await ShareStagedFileAsync(safeFolder, safeName, bytes);
            }
            else
            {
                await SaveInBrowserAsync(safeName, bytes);
            }
        }
        catch (HttpRequestException)
        {
            WarnDownloadFailed();
        }
        catch (OperationCanceledException)
        {
            // No token is passed to the download, so this is the HttpClient timeout, not a
            // disposal. Report it rather than swallowing it: the user tapped and got nothing.
            WarnDownloadFailed();
        }
        catch (Exception)
        {
            // The staging write (IOException, UnauthorizedAccessException), the share sheet and the
            // browser's JS save run here too. This is an OnClick callback on the native heads, where
            // an unhandled exception is fatal to the host, so nothing may escape.
            WarnDownloadFailed();
        }
        finally
        {
            _isExporting = false;
        }
    }

    private void WarnDownloadFailed() => Toast.Warning(FailureMessage ?? L["Snackbar.DownloadFailed"].Value);

    /// <summary>
    /// Resolves the sanitized file name and, on a native head that opted into a staging folder, the
    /// sanitized folder name. False when either has nothing usable left.
    /// </summary>
    private bool TryResolveTarget([NotNullWhen(true)] out string? safeName, out string? safeFolder)
    {
        safeName = ResolveStagedFileName(FileName);
        var stagesInFolder = ExternalLink.InterceptsLinks && StagingFolderName is not null;
        safeFolder = stagesInFolder ? ResolveStagedFileName(StagingFolderName) : null;

        return safeName is not null && (!stagesInFolder || safeFolder is not null);
    }

    private async Task ShareStagedFileAsync(string? safeFolder, string safeName, byte[] bytes)
    {
        var filePath = await StageFileAsync(safeFolder, safeName, bytes);

        if (!await Share.ShareFileAsync(ShareTitle, filePath, ContentType))
        {
            Toast.Warning(UnavailableMessage ?? L["Snackbar.ShareUnavailable"].Value);
        }
    }

    // The bytes are already in memory (the authenticated fetch), so they go to the browser as a
    // stream and file-download.js saves them through a temporary object URL. The reference owns the
    // stream and disposes it.
    private async Task SaveInBrowserAsync(string safeName, byte[] bytes)
    {
        using var content = new DotNetStreamReference(new MemoryStream(bytes));
        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", SaveFileModulePath);
            await using (module.ConfigureAwait(false))
            {
                await module.InvokeVoidAsync("saveFile", safeName, ContentType, content);
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone: there is no browser left to save into or to show the failure toast in.
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

    /// <summary>
    /// Writes the payload into the temp directory, or into the already-sanitized
    /// <paramref name="safeFolder"/> beneath it, under an already-sanitized name.
    /// </summary>
    /// <remarks>
    /// The staged file is not deleted after the share: on Android the share intent returns as soon
    /// as it launches, so deleting here would race the receiving app reading it. The path is
    /// per-entity, so a stale copy from a previous tap is simply replaced; clearing it first keeps a
    /// truncated or half-written leftover from being shared. A dedicated staging folder is emptied of
    /// every file first, so no earlier export survives the next tap.
    /// </remarks>
    private static async Task<string> StageFileAsync(string? safeFolder, string safeName, byte[] bytes)
    {
        var filePath = Path.Combine(Path.GetTempPath(), safeName);

        if (safeFolder is not null)
        {
            var directory = Path.Combine(Path.GetTempPath(), safeFolder);
            Directory.CreateDirectory(directory);
            foreach (var stale in Directory.EnumerateFiles(directory))
            {
                File.Delete(stale);
            }

            filePath = Path.Combine(directory, safeName);
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        await File.WriteAllBytesAsync(filePath, bytes);

        return filePath;
    }
}
