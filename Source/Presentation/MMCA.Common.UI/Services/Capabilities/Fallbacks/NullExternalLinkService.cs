namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// Default <see cref="IExternalLinkService"/>: no interception, so components render plain
/// anchors with <c>target="_blank"</c> — the correct behavior on web heads even without JS.
/// </summary>
public sealed class NullExternalLinkService : IExternalLinkService
{
    /// <inheritdoc />
    public bool InterceptsLinks => false;

    /// <inheritdoc />
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
