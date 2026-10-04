using System.Globalization;
using CommunityToolkit.Maui.Media;
using MMCA.Common.UI.Services.Capabilities.Media;

namespace MMCA.Common.UI.Maui.Capabilities.Media;

/// <summary>
/// MAUI <see cref="ISpeechToTextService"/> over CommunityToolkit.Maui's
/// <see cref="SpeechToText"/> (ADR-042 Wave 4). Owns the microphone/recognition permission
/// flow; denial, cancellation, and recognizer failure all return <see langword="null"/> and
/// the dictation affordance simply does nothing. The toolkit's start/stop listening API is
/// adapted to the contract's single listen-until-final call.
/// </summary>
public sealed class MauiSpeechToTextService : ISpeechToTextService
{
    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<string?> ListenAsync(
        CultureInfo culture,
        IProgress<string>? partialResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(culture);

        try
        {
            if (!await SpeechToText.Default.RequestPermissions(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnUpdated(object? sender, SpeechToTextRecognitionResultUpdatedEventArgs args) =>
                partialResults?.Report(args.RecognitionResult);

            void OnCompleted(object? sender, SpeechToTextRecognitionResultCompletedEventArgs args) =>
                completion.TrySetResult(args.RecognitionResult.IsSuccessful ? args.RecognitionResult.Text : null);

            SpeechToText.Default.RecognitionResultUpdated += OnUpdated;
            SpeechToText.Default.RecognitionResultCompleted += OnCompleted;
            try
            {
                var options = new SpeechToTextOptions
                {
                    Culture = culture,
                    ShouldReportPartialResults = partialResults is not null,
                };

                // The platform recognizer must be created and driven from the main thread (Android's
                // SpeechRecognizer refuses any other), and the permission await above resumes on the
                // thread pool.
                await MainThread.InvokeOnMainThreadAsync(
                    () => SpeechToText.Default.StartListenAsync(options, cancellationToken)).ConfigureAwait(false);

                await using var registration =
                    cancellationToken.Register(() => completion.TrySetResult(null));
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                SpeechToText.Default.RecognitionResultUpdated -= OnUpdated;
                SpeechToText.Default.RecognitionResultCompleted -= OnCompleted;
                await MainThread.InvokeOnMainThreadAsync(
                    () => SpeechToText.Default.StopListenAsync(CancellationToken.None)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every recognizer or permission failure, not only the expected types: the contract
            // promises null, and a throw here surfaces as an unhandled error in the dictating form.
            return null;
        }
    }
}
