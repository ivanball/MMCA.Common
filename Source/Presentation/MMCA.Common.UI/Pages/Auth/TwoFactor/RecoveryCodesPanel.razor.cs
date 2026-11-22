using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Capabilities.Interop;

namespace MMCA.Common.UI.Pages.Auth.TwoFactor;

/// <summary>
/// Shows a freshly issued set of recovery codes once, with a copy button and a "saved" check that
/// enables the way on. The codes are never logged, never put in the URL and never cached here: they
/// live in the parameter for as long as the page shows this state.
/// </summary>
public sealed partial class RecoveryCodesPanel
{
    private ElementReference _heading;
    private bool _saved;
    private bool _focused;

    /// <summary>Gets or sets the recovery codes, in the order the API issued them.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<string> Codes { get; set; } = [];

    /// <summary>Gets or sets the text of the continue button; the default is the localized "Continue".</summary>
    [Parameter]
    public string? ContinueText { get; set; }

    /// <summary>Gets or sets the callback run once the user confirmed the codes are saved.</summary>
    [Parameter]
    public EventCallback OnContinue { get; set; }

    [Inject] private IToastService Toast { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The codes replace the previous state in place, so focus moves to their heading once, or a
        // keyboard or screen-reader user would stay on a control that no longer exists.
        if (!_focused)
        {
            _focused = true;
            await _heading.FocusAsync();
        }
    }

    /// <summary>
    /// Copies the codes, one per line. The clipboard is resolved at the click, so a host that registers
    /// no clipboard capability still renders the panel; a failed copy says so.
    /// </summary>
    private async Task CopyAsync()
    {
        var clipboard = Services.GetService<IClipboardService>();
        var copied = clipboard is not null
            && await clipboard.SetTextAsync(string.Join(Environment.NewLine, Codes));

        if (copied)
        {
            Toast.Success(L["Auth.TwoFactor.RecoveryCodes.Copied"].Value);
        }
        else
        {
            Toast.Error(L["Auth.TwoFactor.GenericError"].Value);
        }
    }
}
