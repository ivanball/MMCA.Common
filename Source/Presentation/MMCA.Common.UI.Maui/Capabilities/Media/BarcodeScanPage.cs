using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace MMCA.Common.UI.Maui.Capabilities.Media;

/// <summary>
/// Modal scan surface for <see cref="MauiBarcodeScannerService"/>: a full-bleed ZXing camera
/// reader with a cancel button underneath and, for a continuous session, an outcome line over the
/// bottom of the camera view. Built in code rather than XAML so the package ships no compiled
/// resource dictionary and the page stays an implementation detail of the service.
/// <para>
/// Detection runs only while the service waits on <see cref="NextDecodeAsync"/>, and stops on the
/// first decode, so each wait yields one payload however many frames the camera decodes meanwhile.
/// </para>
/// <para>
/// Every exit path ends the session through <see cref="Cancel"/> exactly once (cancel button,
/// platform back gesture, the page disappearing, the caller's token, or the service closing it), so
/// the caller always gets an answer and the camera is always released.
/// </para>
/// <para>
/// <see langword="partial"/> is required by CsWinRT1028 on the windows TFM (a ContentPage crosses the WinRT
/// ABI); the redundancy style rules that object to it on the other TFMs are silenced project-wide
/// in the csproj, exactly as the comment there describes.
/// </para>
/// </summary>
internal sealed partial class BarcodeScanPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _ended =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CameraBarcodeReaderView _reader;
    private readonly Label _outcome;
    private readonly Action? _onEnded;

    // The wait a decode completes; replaced by each NextDecodeAsync. 1 while a decode is wanted:
    // the camera raises BarcodesDetected off the main thread, frame after frame, so the exchange
    // lets exactly one frame through per wait.
    private TaskCompletionSource<string?>? _decode;
    private int _wantDecode;

    /// <summary>Builds the page.</summary>
    /// <param name="cancelText">The cancel button's label.</param>
    /// <param name="cameraDescription">The accessible description and title of the scan surface.</param>
    /// <param name="onEnded">Run once when the session ends, on whichever path ends it.</param>
    internal BarcodeScanPage(string cancelText, string cameraDescription, Action? onEnded = null)
    {
        _onEnded = onEnded;
        _reader = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                // Two-dimensional only: the affordance is a QR/DataMatrix scan, and admitting the
                // 1D formats multiplies false positives on a shaky handheld frame.
                Formats = BarcodeFormats.TwoDimensional,
                AutoRotate = true,
                Multiple = false,
            },
            IsDetecting = false,
        };
        _reader.BarcodesDetected += OnBarcodesDetected;
        SemanticProperties.SetDescription(_reader, cameraDescription);

        // Over the bottom of the camera view, just above the cancel button: where the operator's eyes
        // already are when the code leaves the frame. Hidden until there is an outcome to show.
        _outcome = new Label
        {
            IsVisible = false,
            VerticalOptions = LayoutOptions.End,
            HorizontalTextAlignment = TextAlignment.Center,
            Padding = new Thickness(16, 12),
            FontSize = 20,
            TextColor = Colors.White,
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.75),
        };

        var cancel = new Button
        {
            Text = cancelText,
            Margin = new Thickness(16),
        };
        cancel.Clicked += OnCancelClicked;
        SemanticProperties.SetDescription(cancel, cancelText);

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
        };
        layout.Add(_reader, 0, 0);
        layout.Add(_outcome, 0, 0);
        layout.Add(cancel, 0, 1);

        Title = cameraDescription;
        Content = layout;
    }

    /// <summary>Completes when the session ends, on any exit path.</summary>
    internal Task Ended => _ended.Task;

    /// <summary>Gets a value indicating whether the session has ended.</summary>
    internal bool HasEnded => _ended.Task.IsCompleted;

    /// <summary>
    /// Starts detection and completes with the next decoded payload, or with <see langword="null"/>
    /// once the session ends. Call on the main thread, one wait at a time.
    /// </summary>
    internal Task<string?> NextDecodeAsync()
    {
        if (HasEnded)
        {
            return Task.FromResult<string?>(null);
        }

        var decode = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decode = decode;
        Volatile.Write(ref _wantDecode, 1);
        _reader.IsDetecting = true;
        return WaitForDecodeOrEndAsync(decode.Task);
    }

    /// <summary>
    /// Shows <paramref name="text"/> over the camera view and announces it to a screen reader, or
    /// hides the outcome line when it is <see langword="null"/>. Call on the main thread.
    /// </summary>
    /// <param name="text">The outcome, for example "Checked in".</param>
    internal void ShowOutcome(string? text)
    {
        var shown = string.IsNullOrWhiteSpace(text) ? null : text;
        _outcome.Text = shown;
        _outcome.IsVisible = shown is not null;
        SemanticProperties.SetDescription(_outcome, shown);
        if (shown is not null)
        {
            SemanticScreenReader.Default.Announce(shown);
        }
    }

    /// <summary>Ends the session. Safe to call repeatedly and from any exit path; only the first call counts.</summary>
    internal void Cancel()
    {
        if (!_ended.TrySetResult(null))
        {
            return;
        }

        StopDetecting();
        _onEnded?.Invoke();
    }

    /// <inheritdoc />
    protected override bool OnBackButtonPressed()
    {
        // Consume the gesture: the service owns the single PopModalAsync, so letting the platform
        // pop the page itself would leave the service popping whatever page came next.
        Cancel();
        return true;
    }

    /// <inheritdoc />
    protected override void OnDisappearing()
    {
        Cancel();
        base.OnDisappearing();
    }

    private async Task<string?> WaitForDecodeOrEndAsync(Task<string?> decode)
    {
        // ConfigureAwait(true): the service reads the result on the main thread it awaits from.
        var first = await Task.WhenAny(decode, _ended.Task).ConfigureAwait(true);
        return first == decode && !HasEnded ? await decode.ConfigureAwait(true) : null;
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Cancel();

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results?.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Value))?.Value;
        if (value is null || Interlocked.Exchange(ref _wantDecode, 0) == 0)
        {
            return;
        }

        // Pause the camera on the hit: continued detection would keep raising this event while the
        // caller handles the payload (or while the single-scan modal animates away).
        _reader.IsDetecting = false;
        _decode?.TrySetResult(value);
    }

    private void StopDetecting()
    {
        Volatile.Write(ref _wantDecode, 0);
        _reader.BarcodesDetected -= OnBarcodesDetected;
        _reader.IsDetecting = false;
    }
}
