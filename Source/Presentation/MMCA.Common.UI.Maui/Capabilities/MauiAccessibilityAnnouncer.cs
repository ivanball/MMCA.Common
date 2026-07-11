using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IAccessibilityAnnouncer"/> over <c>SemanticScreenReader.Default</c>
/// (TalkBack / VoiceOver / Narrator). Silent no-op when no screen reader is active.
/// </summary>
public sealed class MauiAccessibilityAnnouncer : IAccessibilityAnnouncer
{
    /// <inheritdoc />
    public Task AnnounceAsync(string message, CancellationToken cancellationToken = default)
    {
        try
        {
            SemanticScreenReader.Default.Announce(message);
        }
        catch (FeatureNotSupportedException)
        {
            // No screen-reader integration on this platform — drop the announcement.
        }

        return Task.CompletedTask;
    }
}
