namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Browser <see cref="IAccessibilityAnnouncer"/>: writes into a visually hidden
/// <c>aria-live="polite"</c> region (created on first use by <c>capabilities-interop.js</c>),
/// which every screen reader monitors.
/// </summary>
public sealed class BrowserAccessibilityAnnouncer : IAccessibilityAnnouncer
{
    private readonly CapabilitiesJsModule _module;

    /// <summary>Initializes the announcer over the shared capabilities JS module.</summary>
    public BrowserAccessibilityAnnouncer(CapabilitiesJsModule module) => _module = module;

    /// <inheritdoc />
    public async Task AnnounceAsync(string message, CancellationToken cancellationToken = default) =>
        await _module
            .InvokeOrDefaultAsync<bool?>("announce", [message], cancellationToken)
            .ConfigureAwait(false);
}
