namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Reads text aloud (session descriptions, announcements) using the platform speech
/// synthesizer, matching the active UI culture's voice when one is installed. Web/null
/// fallbacks report <see cref="IsSupported"/> <see langword="false"/> and components hide
/// the affordance.
/// </summary>
public interface ITextToSpeechService
{
    /// <summary>Whether speech synthesis is available on this platform.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Speaks <paramref name="text"/>; completes when playback ends. Cancel the token or call
    /// <see cref="StopAsync"/> to interrupt. Falls back to the default voice when no voice
    /// matches the current culture.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Stops any in-progress speech.</summary>
    Task StopAsync();
}
