using System.Globalization;

namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Dictates speech into text fields (feedback forms, live Q&amp;A questions) using the
/// platform recognizer. Web/null fallbacks report <see cref="IsSupported"/>
/// <see langword="false"/> and components hide the microphone affordance.
/// </summary>
public interface ISpeechToTextService
{
    /// <summary>Whether speech recognition is available on this platform.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Listens until the recognizer finalizes or the token cancels, streaming partial
    /// hypotheses through <paramref name="partialResults"/>. Returns the final transcript,
    /// or <see langword="null"/> on permission denial, cancellation, or recognizer failure.
    /// </summary>
    Task<string?> ListenAsync(
        CultureInfo culture,
        IProgress<string>? partialResults,
        CancellationToken cancellationToken = default);
}
