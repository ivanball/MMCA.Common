namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="ITextToSpeechService"/>: no synthesizer; components hide the affordance.</summary>
public sealed class NullTextToSpeechService : ITextToSpeechService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task SpeakAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;
}
