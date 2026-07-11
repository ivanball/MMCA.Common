namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>Browser <see cref="IClipboardService"/> over <c>navigator.clipboard.writeText</c>.</summary>
public sealed class BrowserClipboardService : IClipboardService
{
    private readonly CapabilitiesJsModule _module;

    /// <summary>Initializes the service over the shared capabilities JS module.</summary>
    public BrowserClipboardService(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var copied = await _module
            .InvokeOrDefaultAsync<bool?>("copyText", [text], cancellationToken)
            .ConfigureAwait(false);
        return copied == true;
    }
}
