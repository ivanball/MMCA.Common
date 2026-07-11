using System.Globalization;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="ITextToSpeechService"/> over <c>TextToSpeech.Default</c>. Picks the first
/// installed voice matching the current UI culture (two-letter language match) and falls back
/// to the platform default voice — devices without an es voice still speak rather than throw.
/// MAUI exposes no stop API, so <see cref="StopAsync"/> cancels the in-flight utterance's token.
/// </summary>
public sealed partial class MauiTextToSpeechService : ITextToSpeechService, IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _activeUtterance;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await StopAsync().ConfigureAwait(false);

        var utterance = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _activeUtterance = utterance;
        }

        try
        {
            var options = new SpeechOptions
            {
                Locale = await MatchLocaleAsync(CultureInfo.CurrentUICulture).ConfigureAwait(false),
            };
            await TextToSpeech.Default.SpeakAsync(text, options, utterance.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped by StopAsync or the caller's token — expected.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeUtterance, utterance))
                {
                    _activeUtterance = null;
                }
            }

            utterance.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            active = _activeUtterance;
        }

        if (active is null)
        {
            return;
        }

        try
        {
            await active.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Utterance completed concurrently; nothing to stop.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _activeUtterance?.Dispose();
            _activeUtterance = null;
        }
    }

    private static async Task<Locale?> MatchLocaleAsync(CultureInfo culture)
    {
        try
        {
            var locales = await TextToSpeech.Default.GetLocalesAsync().ConfigureAwait(false);
            return locales.FirstOrDefault(locale =>
                string.Equals(locale.Language, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
        }
        catch (FeatureNotSupportedException)
        {
            return null;
        }
    }
}
