using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace MMCA.Common.UI.Components.Forms;

/// <summary>
/// A file picker a keyboard can reach: a visually hidden <c>&lt;input type="file"&gt;</c> that keeps its
/// tab stop, a <c>&lt;label for&gt;</c> styled as a <see cref="MudButton"/> that opens it on click, and a
/// focus ring on the wrapper while the input has focus (WCAG 2.1.1 and 2.4.7). Hiding the input with
/// the <c>hidden</c> attribute instead removes it from the tab order, so a keyboard user cannot open
/// the picker at all.
/// </summary>
public partial class FilePickerButton
{
    /// <summary>Gets or sets the visible button text, which is also the input's accessible name.</summary>
    [Parameter]
    [EditorRequired]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the callback invoked with the files the user picked.</summary>
    [Parameter]
    [EditorRequired]
    public EventCallback<InputFileChangeEventArgs> OnChange { get; set; }

    /// <summary>
    /// Gets or sets the input's <c>id</c>. Leave it unset for a generated one; set it when a test or a
    /// page selects the input by id (for example Playwright's <c>SetInputFilesAsync("#id", ...)</c>).
    /// </summary>
    [Parameter]
    public string? InputId { get; set; }

    /// <summary>
    /// Gets or sets the <c>accept</c> list. Name the MIME types as well as the extensions: the Android
    /// picker filters on MIME types only.
    /// </summary>
    [Parameter]
    public string? Accept { get; set; }

    /// <summary>
    /// Gets or sets an accessible name for the input that differs from <see cref="Text"/>. Leave it
    /// unset to let the label text name the input.
    /// </summary>
    [Parameter]
    public string? AriaLabel { get; set; }

    /// <summary>Gets or sets a value indicating whether the picker is disabled.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Gets or sets a value indicating whether more than one file may be picked.</summary>
    [Parameter]
    public bool Multiple { get; set; }

    /// <summary>Gets or sets the button's start icon. Defaults to the upload glyph.</summary>
    [Parameter]
    public string Icon { get; set; } = Icons.Material.Filled.Upload;

    /// <summary>Gets or sets the button variant. Defaults to outlined.</summary>
    [Parameter]
    public Variant Variant { get; set; } = Variant.Outlined;

    /// <summary>Gets or sets the button color.</summary>
    [Parameter]
    public Color Color { get; set; } = Color.Default;

    /// <summary>Gets or sets the button size.</summary>
    [Parameter]
    public Size Size { get; set; } = Size.Medium;

    /// <summary>Gets or sets extra CSS classes for the wrapper.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>
    /// Gets or sets extra attributes for the <c>&lt;input type="file"&gt;</c> (for example a
    /// <c>data-testid</c>).
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? InputAttributes { get; set; }

    private string GeneratedInputId { get; } = $"file-picker-{Guid.NewGuid():N}";

    private string EffectiveInputId => string.IsNullOrWhiteSpace(InputId) ? GeneratedInputId : InputId;
}
