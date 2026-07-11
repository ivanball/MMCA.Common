namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="IExternalLinkService"/>: anchors keep their native
/// <c>target="_blank"</c> behavior (<see cref="InterceptsLinks"/> is <see langword="false"/>);
/// programmatic opens (maps fallback) go through <c>window.open</c>.
/// </summary>
public sealed class BrowserExternalLinkService : IExternalLinkService
{
    private readonly CapabilitiesJsModule _module;

    /// <summary>Initializes the service over the shared capabilities JS module.</summary>
    public BrowserExternalLinkService(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public bool InterceptsLinks => false;

    /// <inheritdoc />
    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        await _module
            .InvokeOrDefaultAsync<bool?>("openExternal", [uri.ToString()], cancellationToken)
            .ConfigureAwait(false);
    }
}
