using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// Modal scan surface for <see cref="MauiBarcodeScannerService"/>: a full-bleed ZXing camera
/// reader with a cancel button underneath. Built in code rather than XAML so the package ships no
/// compiled resource dictionary and the page stays an implementation detail of the service.
/// <para>
/// Every exit path resolves the same completion source exactly once (first decode, cancel button,
/// platform back gesture, or the page disappearing), so the caller always gets an answer and the
/// camera is always released.
/// </para>
/// <para>
/// <c>partial</c> is required by CsWinRT1028 on the windows TFM (a ContentPage crosses the WinRT
/// ABI); the redundancy style rules that object to it on the other TFMs are silenced project-wide
/// in the csproj, exactly as the comment there describes.
/// </para>
/// </summary>
internal sealed partial class BarcodeScanPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CameraBarcodeReaderView _reader;

    internal BarcodeScanPage(string cancelText, string cameraDescription)
    {
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
            IsDetecting = true,
        };
        _reader.BarcodesDetected += OnBarcodesDetected;
        SemanticProperties.SetDescription(_reader, cameraDescription);

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
        layout.Add(cancel, 0, 1);

        Title = cameraDescription;
        Content = layout;
    }

    /// <summary>Completes with the first decoded payload, or <see langword="null"/> on any cancel.</summary>
    internal Task<string?> Completion => _completion.Task;

    /// <summary>Resolves the scan as cancelled. Safe to call repeatedly and from any exit path.</summary>
    internal void Cancel() => _completion.TrySetResult(null);

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
        StopDetecting();
        Cancel();
        base.OnDisappearing();
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Cancel();

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results?.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Value))?.Value;
        if (value is null)
        {
            return;
        }

        // Stop the camera on the first hit: continued detection would keep raising this event
        // against an already-completed source while the modal animates away.
        StopDetecting();
        _completion.TrySetResult(value);
    }

    private void StopDetecting()
    {
        _reader.BarcodesDetected -= OnBarcodesDetected;
        _reader.IsDetecting = false;
    }
}
