using MMCA.Common.UI.Common.Interfaces;
using MudBlazor;

namespace MMCA.Common.UI.Services;

/// <summary>
/// The MudBlazor-backed <see cref="IToastService"/>: one of only two types in the framework that
/// name a component-library service (its sibling is <see cref="MudAppDialogService"/>). Registered
/// by <c>AddUIShared</c>, so every host that calls it gets toasts without any page having to know
/// which library renders them.
/// </summary>
internal sealed class MudToastService(ISnackbar snackbar) : IToastService
{
    /// <inheritdoc />
    public void Success(string message) => snackbar.Add(message, Severity.Success);

    /// <inheritdoc />
    public void Info(string message) => snackbar.Add(message, Severity.Info);

    /// <inheritdoc />
    public void Warning(string message) => snackbar.Add(message, Severity.Warning);

    /// <inheritdoc />
    public void Error(string message) => snackbar.Add(message, Severity.Error);

    /// <inheritdoc />
    public void Show(string message, ToastSeverity severity) => snackbar.Add(message, Map(severity));

    /// <inheritdoc />
    public void ShowPersistent(string title, string body, ToastSeverity severity = ToastSeverity.Info) =>
        snackbar.Add(
            builder =>
            {
                builder.OpenElement(0, "strong");
                builder.AddContent(1, title);
                builder.CloseElement();
                builder.OpenElement(2, "br");
                builder.CloseElement();
                builder.AddContent(3, body);
            },
            Map(severity),
            options =>
            {
                // The message arrived unprompted (a push), so it must survive until the user has
                // actually looked at the screen rather than expiring on the default timer.
                options.RequireInteraction = true;
                options.SnackbarVariant = Variant.Filled;
            });

    /// <inheritdoc />
    public void ShowAction(
        string message,
        string actionText,
        Func<Task> onAction,
        ToastSeverity severity = ToastSeverity.Info,
        bool requireInteraction = false) =>
        snackbar.Add(
            message,
            Map(severity),
            options =>
            {
                options.Action = actionText;
                options.ActionColor = Color.Primary;

                // MudBlazor hands the click a Snackbar instance the callback has no use for: the
                // action is described entirely by the delegate the caller passed.
                options.OnClick = _ => onAction();

                if (requireInteraction)
                {
                    // Stated outright rather than left to MudBlazor's null default (which already
                    // pins an action snackbar open): the contract promises the toast waits, so it
                    // must not depend on a host configuration the caller cannot see. The filled
                    // variant is the same emphasis convention ShowPersistent uses.
                    options.RequireInteraction = true;
                    options.SnackbarVariant = Variant.Filled;
                }
            });

    /// <summary>
    /// Projects the vendor-neutral level onto MudBlazor's own. Written out rather than cast: the
    /// two enums happen to agree numerically today, and an implicit dependency on that would break
    /// silently the day either side gains a member.
    /// </summary>
    private static Severity Map(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Normal => Severity.Normal,
        ToastSeverity.Info => Severity.Info,
        ToastSeverity.Success => Severity.Success,
        ToastSeverity.Warning => Severity.Warning,
        ToastSeverity.Error => Severity.Error,
        _ => Severity.Normal,
    };
}
