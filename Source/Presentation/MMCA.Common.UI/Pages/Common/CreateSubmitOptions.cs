using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Per-call options for <see cref="CreatePageBase.SubmitAsync{TCreated}"/>.
/// </summary>
public sealed record CreateSubmitOptions
{
    /// <summary>
    /// Gets a check that runs after the form validated and before the save: it returns a warning
    /// message to stop the submit (shown as a warning toast), or <see langword="null"/> to continue.
    /// </summary>
    public Func<string?>? PreCheck { get; init; }

    /// <summary>
    /// Gets a value indicating whether a failed save raises the composed failure toast. Defaults to
    /// <see langword="true"/>; a page that shows the failure some other way passes <see langword="false"/>.
    /// </summary>
    public bool ToastOnFailure { get; init; } = true;

    /// <summary>
    /// Gets the localizer the failure toast resolves each error message through (passed to
    /// <c>NotifyOnFailure</c>); <see langword="null"/> shows the messages as they are.
    /// </summary>
    public IStringLocalizer? Localizer { get; init; }
}
