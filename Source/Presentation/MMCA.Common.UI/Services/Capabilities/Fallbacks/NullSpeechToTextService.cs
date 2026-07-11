using System.Globalization;

namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="ISpeechToTextService"/>: no recognizer; components hide the microphone.</summary>
public sealed class NullSpeechToTextService : ISpeechToTextService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<string?> ListenAsync(
        CultureInfo culture,
        IProgress<string>? partialResults,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
