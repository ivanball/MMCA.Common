namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="IShareService"/> over <c>navigator.share</c>. Reports
/// <see langword="false"/> where the Web Share API is unavailable (desktop Firefox,
/// insecure contexts) so callers fall back to copy-link; file sharing is unsupported.
/// </summary>
public sealed class BrowserShareService : IShareService
{
    private readonly CapabilitiesJsModule _module;

    /// <summary>Initializes the service over the shared capabilities JS module.</summary>
    public BrowserShareService(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public async Task<bool> ShareLinkAsync(string title, Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var shared = await _module
            .InvokeOrDefaultAsync<bool?>("shareLink", [title, uri.ToString()], cancellationToken)
            .ConfigureAwait(false);
        return shared == true;
    }

    /// <inheritdoc />
    public Task<bool> ShareFileAsync(string title, string filePath, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
