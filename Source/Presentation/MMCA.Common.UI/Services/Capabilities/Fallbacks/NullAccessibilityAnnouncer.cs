namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IAccessibilityAnnouncer"/>: announcements are dropped.</summary>
public sealed class NullAccessibilityAnnouncer : IAccessibilityAnnouncer
{
    /// <inheritdoc />
    public Task AnnounceAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
